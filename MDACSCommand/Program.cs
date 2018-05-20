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
        SemaphoreSlim entryCountLock;
        SemaphoreSlim critical;
        List<CommandWaitResponseEntry> entries;

        public CommandQueueGroup() {
            critical = new SemaphoreSlim(1, 1);
            entryCountLock = new SemaphoreSlim(0, 0xffff);
            entries = new List<CommandWaitResponseEntry>();
        }



        public async Task Add(CommandWaitResponseEntry entry) {
            await critical.WaitAsync();
            entries.Add(entry);
            entryCountLock.Release();
            critical.Release();
        }

        public async Task<int> Count() {
            await critical.WaitAsync();
            var cnt = entries.Count;
            critical.Release();
            return cnt;
        }

        /// <remark>
        /// The way this works is that each time an item is added it increments the
        /// `entryCountLock` and allows one thread of execution to enter. Each thread
        /// that enters grabs all avaliable entries which throws off the `entryCountLock`
        /// but the only side effect of that is the next thread that goes in will just have
        /// to spin off the extra count before it finally stops waiting for an actual entry.
        ///
        /// I would have prefered to have used something more efficient and I am sure that
        /// there is a way to do it. I simply need a Sempahore that can be released multiple
        /// times without throwing an exception or exceeding its maximum count.
        /// </remark>
        public async Task<CommandWaitResponseEntry[]> WaitAndGetCommands() {
            CommandWaitResponseEntry[] ret;

            do {
                await entryCountLock.WaitAsync();
                await critical.WaitAsync();
                ret = entries.ToArray();
                entries.Clear();
                critical.Release();
            } while (ret.Length < 1);

            return ret;
        }
    }

    struct CommandResponseStored {
        public long createTime;
        public string commandResponse;
    }

    class ServerHandler {
        ProgramConfig config;
        Dictionary<string, CommandQueueGroup> groups;
        HashSet<string> usedIds;
        Dictionary<string, string> guidToId;
        Dictionary<string, CommandResponseStored> responses;

        public ServerHandler(ProgramConfig config) {
            this.config = config;
            this.groups = new Dictionary<string, CommandQueueGroup>();
            this.usedIds = new HashSet<string>();
            this.responses = new Dictionary<string, CommandResponseStored>();
        }

        string GetNewCommandId() {
            var guid = Guid.NewGuid();
            return guid.ToString();
        }

        public async Task<Task> CommandResponseTakeHandler(ServerHandler shandler, HTTPRequest request, Stream body, IProxyHTTPEncoder encoder) {
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

            var req = JsonConvert.DeserializeObject<CommandResponseTakeRequest>(auth.payload);
            var reply = new CommandResponseTakeResponse() {
                responses = new Dictionary<string, string>(),
            };

            foreach (var guid in req.commandIds) {
                if (responses.ContainsKey(guid)) {
                    reply.responses[guid] = responses[guid].commandResponse;
                } else {
                    reply.responses[guid] = null;
                }
            }

            await encoder.WriteQuickHeader(200, "OK");
            await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(reply));            
            
            return Task.CompletedTask;
        }

        public async Task<Task> CommandResponseWriteHandler(ServerHandler shandler, HTTPRequest request, Stream body, IProxyHTTPEncoder encoder) {
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

            var req = JsonConvert.DeserializeObject<CommandResponseWriteRequest>(auth.payload);
            
            // The initially used GUID per service is kept secret for each service. However,
            // at the moment the exact service the GUID maps to is not stored. Storing and
            // using that would increase the security.
            // TODO: increase security
            // SECURITY: utilize the GUID for commmand response writing
            if (!usedIds.Contains(req.serviceGuid)) {
                await encoder.WriteQuickHeader(403, "Must be authenticated.");
                await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                    JObject.FromObject(new {
                        success = false,
                    })
                ));
                return Task.CompletedTask;                
            }

            var curFileTime = DateTime.Now.ToFileTimeUtc();

            var toRemove = new List<string>();

            foreach (var pair in this.responses) {
                if (curFileTime - pair.Value.createTime > 1000 * 60 * 60 * 24 * 7) {
                    toRemove.Add(pair.Key);
                }
            }

            foreach (var key in toRemove) {
                this.responses.Remove(key);
            }

            foreach (var pair in req.responses) {
                this.responses[pair.Key] = new CommandResponseStored() {
                    createTime = curFileTime,
                    commandResponse = pair.Value,
                };
            }

            await encoder.WriteQuickHeader(200, "OK");
            await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                JObject.FromObject(new {
                    success = true,
                })
            ));

            return Task.CompletedTask;
        }        
        public async Task<Task> CommandExecuteHandler(ServerHandler shandler, HTTPRequest request, Stream body, IProxyHTTPEncoder encoder) {
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

            if (!await UserHasPrivilegeRegisterCommandService(auth.user) && !auth.user.admin)
            {
                await encoder.WriteQuickHeader(403, "Must be admin or have privRegisterService set to true.");
                await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                    JObject.FromObject(new {
                        success = false,
                    })
                ));
                return Task.CompletedTask;
            }

            var req = JsonConvert.DeserializeObject<CommandExecuteRequest>(auth.payload);

            if (!groups.ContainsKey(req.serviceId)) {
                await encoder.WriteQuickHeader(404, "Service does not exist");
                await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                    JObject.FromObject(new {
                        success = false,
                    })
                ));
                return Task.CompletedTask;
            }

            await groups[req.serviceId].Add(new CommandWaitResponseEntry() {
                command = req.command,
                user = auth.user,
                id = GetNewCommandId(),
            });

            await encoder.WriteQuickHeader(200, "Command has been queued");
            await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                JObject.FromObject(new {
                    success = true,
                })
            ));

            return Task.CompletedTask;
        }
        /// <summary>
        /// Return true if the provided user has the privilege to register a command service.
        /// </summary>
        /// <remarks>
        /// Uses reflection to support future implementation of a property that determines the privilege.
        /// </remarks>
        public async Task<bool> UserHasPrivilegeRegisterCommandService(Auth.User user) {
            var userType = user.GetType();

            var propInfo = userType.GetProperty("privRegisterCommandService");

            if (propInfo == null) {
                return true;
            }

            var valueObject = propInfo.GetValue(user);

            if (valueObject == null) {
                return true;
            }

            if (!valueObject.GetType().Name.Equals("bool")) {
                return true;
            }

            return (bool)valueObject;
        }

        public async Task<Task> CommandWaitHandler(ServerHandler shandler, HTTPRequest request, Stream body, IProxyHTTPEncoder encoder) {
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

            if (!await UserHasPrivilegeRegisterCommandService(auth.user) && !auth.user.admin)
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

            var actualId = GetCompleteWaitId(req.serviceId, req.serviceGuid);

            if (!groups.ContainsKey(actualId)) {
                await encoder.WriteQuickHeader(404, "No such queue exists");
                await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                    new CommandWaitResponse() {
                        success = false,
                        commands = null,
                    }
                ));

                return Task.CompletedTask;
            }

            var cmdWaitTask = groups[actualId].WaitAndGetCommands();
            if (Task.WaitAny(cmdWaitTask, Task.Delay(req.timeout)) == 1) {
                await encoder.WriteQuickHeader(200, "Timeout");
                await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                    new CommandWaitResponse() {
                        success = false,
                        commands = null,
                    }
                ));

                return Task.CompletedTask;
            }

            await encoder.WriteQuickHeader(200, "Ok with items");
            await encoder.BodyWriteSingleChunk(JsonConvert.SerializeObject(
                new CommandWaitResponse() {
                    success = true,
                    commands = cmdWaitTask.Result,
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
        public string GetCompleteWaitId(string serviceId, string guid) {
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


    class CommandExecuteRequest {
        public string command;
        public string serviceId;
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
        public string serviceGuid;
        /// <summary>
        /// The maximum time to wait in milliseconds for the request before returning a
        /// non-success code.
        /// </summary>
        public int timeout;
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

    class CommandResponseTakeResponse {
        public Dictionary<string, string> responses;
    }

    class CommandResponseTakeRequest {
        /// <summary>
        /// The list of identifiers representing each command to receive the response for.
        /// </summary>
        public string[] commandIds;
    }
    /// <summary>
    /// Request to provide results for executed commands.
    /// </summary>
    class CommandResponseWriteRequest {
        public string serviceId;
        public string serviceGuid;
        /// <summary>
        /// Provide results using command GUID as the key and the result data as the value.
        /// </summary>
        public Dictionary<string, string> responses;
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
            handlers.Add("/command-response-write", handler.CommandResponseWriteHandler);
            handlers.Add("/command-response-take", handler.CommandResponseTakeHandler);

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
