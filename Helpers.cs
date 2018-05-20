using System;
using System.Threading.Tasks;
using System.Text;
using System.IO;
using static MDACS.API.Auth;
using static MDACS.API.Database;

namespace MDACS.Command
{
    internal class Helpers
    {
        public static async Task<API.Responses.AuthCheckResponse> ReadMessageFromStreamAndAuthenticate(string authUrl, int max_size, Stream input_stream)
        {
            var buf = new byte[1024 * 32];
            int pos = 0;

            var a = input_stream.CanRead;
            var b = input_stream.CanTimeout;

            Console.WriteLine("Reading authenticated payload.");

            while (pos < buf.Length)
            {
                var cnt = await input_stream.ReadAsync(buf, pos, buf.Length - pos);

                if (cnt < 1)
                {
                    break;
                }

                pos += cnt;
            }

            Console.WriteLine("Done reading authenticated payload.");

            var buf_utf8_string = Encoding.UTF8.GetString(buf, 0, pos);

            Console.WriteLine(buf_utf8_string);

            var resp = await MDACS.API.Auth.AuthenticateMessageAsync(
                authUrl,
                buf_utf8_string
            );

            Console.WriteLine("Handing back result from authenticated payload.");

            return resp;
        }

        public static bool CanUserModifyItem(User user, Item item)
        {
            if (user == null || item == null || item.userstr == null)
            {
                return false;
            }

            if (user.admin || user.userfilter == null)
            {
                return true;
            }

            if (item.userstr.IndexOf(user.userfilter) == 0)
            {
                return true;
            }

            return false;
        }

        public static bool CanUserSeeItem(User user, Item item)
        {
            return CanUserModifyItem(user, item);
        }

        public static async Task WriteMessageToStream(Stream output_stream, String out_string)
        {
            var out_buffer = Encoding.UTF8.GetBytes(out_string);
            await output_stream.WriteAsync(out_buffer, 0, out_buffer.Length);
        }
    }
}
