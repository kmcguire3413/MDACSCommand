///
/// This is a rather simple service. In a nutshell, it provides command and command result storage which is accessed
/// by other services and clients. This service centralizes command and control and through that it also is able to
/// centralized configuration management if the client services support configuration rewrite or changes through 
/// commands.
///
/// The API has only three functions. One function adds a command which is then fetched by another service using the
/// command wait function and finally returns the response using the command response function. Security is provided
/// by only allowing clients which authenticate with a user that allows command registration. This prevents normal
/// clients from impersonating a service and receiving sensitive commands.
///
using System;
using System.Net;
using System.Net.Http;
using MDACS.API;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Net.Sockets;
using System.Threading;
using System.Net.Security;
using MDACS.Server;
using Newtonsoft.Json.Linq;
using MDACSAPI;
using System.Threading.Tasks;
using MDACS.API;
using System.Text;

namespace MDACS.Command
{
    class CommandQueueGroup {
        SemaphoreSlim signalChange;
        List<CommandWaitResponseEntry> entries;

        public CommandQueueGroup() {
            signalChange = new SemaphoreSlim(1);
            entries = new List<CommandWaitResponseEntry>();
        }

        public void Add(CommandWaitResponseEntry entry) {
            entries.Add(entry);
        }

        public int Count() {
            return entries.Count;
        }

        public async Task WaitForChangeAsync() {
            await signalChange.WaitAsync();
        }

        public CommandWaitResponseEntry[] TakeArray() {
            return entries.ToArray();
        }
    }

    class ServerHandler {
        ProgramConfig config;
        Dictionary<string, CommandQueueGroup> groups;
        HashSet<string> usedIds;
        Dictionary<string, string> guidToId;

        public ServerHandler(ProgramConfig config) {
            this.config = config;
            this.groups = new Dictionary<string, CommandQueueGroup>();
            this.usedIds = new HashSet<string>();
        }

        public async Task<Task> CommandResponseHandler(ServerHandler shandler, HTTPRequest request, Stream body, IProxyHTTPEncoder encoder) {
            return Task.CompletedTask;
        }        
        public async Task<Task> CommandExecuteHandler(ServerHandler shandler, HTTPRequest request, Stream body, IProxyHTTPEncoder encoder) {
            // look for identifier
                // if not registered then return an error
            // place the command in the appropriate queue
            return Task.CompletedTask;
        }
        public async Task<Task> CommandWaitHandler(ServerHandler shandler, HTTPRequest request, Stream body, IProxyHTTPEncoder encoder) {
            // authenticate request
            // register the provided string as our identifier and append a number to make
            //      the identifier unique if needed
            // block here until a command becomes avalible or a timeout happens
            var auth = await Helpers.ReadMessageFromStreamAndAuthenticate(config.authUrl, 1024 * 16, body);

            if (!auth.success)
            {
                await encoder.WriteQuickHeader(403, "Must be authenticated.");
                await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                    JObject.FromObject(new {
                        success = false,
                    })
                ));
                return Task.CompletedTask;
            }

            if (auth.user.privRegisterCommandService == false && auth.user.admin == false)
            {
                await encoder.WriteQuickHeader(403, "Must be admin or have privRegisterService set to true.");
                await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                    JObject.FromObject(new {
                        success = false,
                    })
                ));
                return Task.CompletedTask;
            }

            var req = JsonConvert.DeserializeObject<CommandWaitRequest>(auth.payload);

            var actualId = getCompleteWaitId(req.serviceId, req.guid);

            if (!groups.ContainsKey(actualId) || groups[actualId].Count() == 0) {
                //
                await groups[actualId].WaitForChangeAsync();

                //await encoder.WriteQuickHeader(200, "OK WITH ZERO ITEMS");
                //await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                //    new CommandWaitResponse() {
                //        success = true,
                //        commands = new List<CommandWaitResponseEntry>().ToArray(),
                //    }
                //));

                return Task.CompletedTask;
            }

            await encoder.WriteQuickHeader(200, "OK WITH ITEMS");
            await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                new CommandWaitResponse() {
                    success = true,
                    commands = groups[actualId].TakeArray(),
                }
            ));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Uses the provided service identifier and the GUID to provide a consistent
        /// and non-conflicting identifier. Sometimes multiple services may present with
        /// the same identifier and this helper function provides consistent resolution
        /// for those cases.
        /// </summary>
        public string getCompleteWaitId(string serviceId, string guid) {
            var id = serviceId;

            if (guidToId.ContainsKey(guid)) {
                return guidToId[guid];
            }

            for (int num = 0; usedIds.Contains(id); ++num) {
                id = $"{serviceId}.{num}";
            }

            guidToId[id] = guid;
            usedIds.Add(id);

            return id;
        }
    }

    class CommandWaitRequest {
        /// <summary>
        /// The service identifier represents the service waiting using a human readable
        /// descriptive string. The string should have no whitespace so that it is not
        /// ambigious with the command string that it may be used within.
        /// </summary>
        public string serviceId;
        /// <summary>
        /// An identifier that should be globally unique. It helps to create consistent
        /// suffixes for conflicting service identifiers.
        /// </summary>
        public string guid;
        /// <summary>
        /// The maximum time to wait in seconds for the request before returning a
        /// non-success code.
        /// </summary>
        public uint timeout;
    }

    /// <summary>
    /// Each command to be executed has to associated user that provided
    /// the command. A client has to authenticate to provide commands and
    /// attaching the user information to each command is cheap and useful.
    /// </summary>
    class CommandWaitResponseEntry {
        public string command;
        public Auth.User user;
        /// <summary>
        /// The id for each command is unique and internal to the command service. The
        /// id is used to associate command responses with commands.
        /// </summary>
        public string id;
    }

    /// <summary>
    /// Each proper response provides zero or more commands that needs to
    /// be executed and have responses returned.
    /// </summary>
    class CommandWaitResponse {
        public bool success;
        public CommandWaitResponseEntry[] commands;
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Provide path or file that contains the JSON configuration. If file does not exit then default one will be created.");
                return;
            }

            if (!File.Exists(args[0])) {
                var tmp = new ProgramConfig() {
                    authUrl = "htts://can.be.https.something.org",
                    port = 34009,
                    sslCertPass = "",
                    sslCertPath = ""
                };

                var tmpFs = File.OpenWrite(args[0]);
                var tmpString = JsonConvert.SerializeObject(tmp);
                var tmpBytes = Encoding.UTF8.GetBytes(tmpString);
                tmpFs.Write(tmpBytes, 0, tmpBytes.Length);
                tmpFs.Close();

                Console.WriteLine($"A descriptive but unusable configuration created at {args[0]}.");
                return;
            }

            var cfgfp = File.OpenText(args[0]);

            var cfg = JsonConvert.DeserializeObject<ProgramConfig>(cfgfp.ReadToEnd());

            cfgfp.Dispose();

            var handler = new ServerHandler(cfg);
            var handlers = new Dictionary<String, SimpleServer<ServerHandler>.SimpleHTTPHandler>();

            handlers.Add("/command-wait", handler.CommandWaitHandler);
            handlers.Add("/command-execute", handler.CommandExecuteHandler);
            handlers.Add("/command-response", handler.CommandResponseHandler);

            var server = SimpleServer<ServerHandler>.Create(
                handler,
                handlers,
                cfg.port,
                cfg.sslCertPath,
                cfg.sslCertPass
            );

            var a = new Thread(() =>
            {
                server.Wait();
            });

            a.Start();
            a.Join();

            // Please do not let me forget this convulted retarded sequence to get from PEM to PFX with the private key.
            // openssl crl2pkcs7 -nocrl -inkey privkey.pem -certfile fullchain.pem -out test.p7b
            // openssl pkcs7 -print_certs -in test.p7b -out test.cer
            // openssl pkcs12 -export -in test.cer -inkey privkey.pem -out test.pfx -nodes
            // THEN... for Windows, at least, import into cert store, then export with private key and password.
            // FINALLY... use the key now and make sure its X509Certificate2.. notice the 2 on the end? Yep.
        }
    }
}
