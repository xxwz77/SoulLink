using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.WindowingTest
{
    public sealed class WindowMessage
    {
        public string Type { get; set; } = "";
        public string Id { get; set; } = "";
        public long Hwnd { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Visible { get; set; }
        public bool Activate { get; set; }
        public string Text { get; set; } = "";
        public long OwnerHwnd { get; set; }
        public System.Threading.SemaphoreSlim WriteLock { get; } = new(1,1);
    }

    public static class Trace
    {
        public static void Write(string source,string text)
        {
            try{var dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SoulLink-Windowing-Test");System.IO.Directory.CreateDirectory(dir);System.IO.File.AppendAllText(System.IO.Path.Combine(dir,"trace.log"),DateTime.Now.ToString("HH:mm:ss.fff")+" "+source+" "+text+Environment.NewLine);}catch{}
        }
    }

    public static class JsonPipe
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static NamedPipeServerStream CreateServer(string name)
            => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        public static async Task WriteAsync(Stream stream, WindowMessage message, CancellationToken token)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            var header = BitConverter.GetBytes(payload.Length);
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        public static async Task<WindowMessage?> ReadAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[4];
            if (!await ReadExactAsync(stream, header, token).ConfigureAwait(false)) return null;
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Invalid IPC message length");
            var payload = new byte[length];
            if (!await ReadExactAsync(stream, payload, token).ConfigureAwait(false)) return null;
            return JsonSerializer.Deserialize<WindowMessage>(payload, Json);
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }
    }
}
