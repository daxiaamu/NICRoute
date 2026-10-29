using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;

namespace NICRoute.Core;

public sealed record ObservedConnection(string Id, string ProcessPath, uint ProcessId, string Network,
    string Destination, string Outbound, string[] Chain, long Uploaded, long Downloaded, bool Closed);
public sealed record ObservedConnectionEvent(int Kind, string Id, ObservedConnection? Connection, long UploadDelta, long DownloadDelta);
public sealed record ObservedConnectionBatch(bool Reset, ObservedConnectionEvent[] Events);

/// <summary>Read-only subset of sing-box 1.14.4 daemon/started_service.proto.
/// gRPC-Web framing and protobuf wire format are bounded; unknown fields are skipped.
/// No generated upstream code or additional runtime packages are required.</summary>
public static class ConnectionWire
{
    public const int MaximumFrame = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] SubscriptionRequest()
    {
        // SubscribeConnectionsRequest.interval is a Go duration in nanoseconds: one second.
        return [0, 0, 0, 0, 6, 8, 0x80, 0x94, 0xEB, 0xDC, 3];
    }
    public static ObservedConnectionBatch Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("连接数据帧超过大小限制。");
        var reader = new Reader(bytes); var events = new List<ObservedConnectionEvent>(); var reset = false;
        while (reader.Next())
        {
            if (reader.Field == 1)
            {
                if (events.Count >= 50000) throw new InvalidDataException("连接事件过多。");
                events.Add(ParseEvent(reader.Data()));
            }
            else if (reader.Field == 2) reset = reader.Number() != 0;
        }
        return new(reset, events.ToArray());
    }
    private static ObservedConnectionEvent ParseEvent(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes); var kind = 0; var id = ""; ObservedConnection? connection = null; long up = 0, down = 0;
        while (reader.Next())
            switch (reader.Field)
            {
                case 1: kind = checked((int)reader.Number()); break;
                case 2: id = reader.Text(); break;
                case 3: connection = ParseConnection(reader.Data()); break;
                case 4: up = reader.Counter(); break;
                case 5: down = reader.Counter(); break;
            }
        if (id.Length == 0 || id.Length > 128 || kind is < 0 or > 2 || connection is not null && connection.Id != id)
            throw new InvalidDataException("连接事件标识无效。");
        return new(kind, id, connection, up, down);
    }
    private static ObservedConnection ParseConnection(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes); var id = ""; var path = ""; uint pid = 0; var network = "";
        var destination = ""; var outbound = ""; var chain = new List<string>(); long uploaded = 0, downloaded = 0; var closed = false;
        while (reader.Next())
            switch (reader.Field)
            {
                case 1: id = reader.Text(); break;
                case 5: network = reader.Text(); break;
                case 7: destination = reader.Text(); break;
                case 13: closed = reader.Number() != 0; break;
                case 16: uploaded = reader.Counter(); break;
                case 17: downloaded = reader.Counter(); break;
                case 19: outbound = reader.Text(); break;
                case 21:
                    if (chain.Count >= 64) throw new InvalidDataException("连接出口链过长。");
                    chain.Add(reader.Text()); break;
                case 22:
                    var process = new Reader(reader.Data());
                    while (process.Next())
                        if (process.Field == 1) pid = checked((uint)process.Number());
                        else if (process.Field == 4) path = process.Text();
                    break;
            }
        return new(id, path, pid, network, destination, outbound, chain.ToArray(), uploaded, downloaded, closed);
    }
    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        private int offset;
        private int wire;
        private ulong number;
        private ReadOnlySpan<byte> data;
        public int Field { get; private set; }
        public bool Next()
        {
            if (offset == bytes.Length) return false;
            var tag = Varint(); Field = checked((int)(tag >> 3)); wire = (int)(tag & 7);
            if (Field <= 0) throw new InvalidDataException("protobuf 字段无效。");
            switch (wire)
            {
                case 0: number = Varint(); break;
                case 1: Slice(8); break;
                case 2:
                    var length = Varint();
                    if (length > MaximumFrame) throw new InvalidDataException("protobuf 内容过长。");
                    data = Slice((int)length); break;
                case 5: Slice(4); break;
                default: throw new InvalidDataException("protobuf 编码无效。");
            }
            return true;
        }
        private ReadOnlySpan<byte> Slice(int length)
        {
            if (length > bytes.Length - offset) throw new InvalidDataException("protobuf 内容不完整。");
            var value = bytes.Slice(offset, length); offset += length; return value;
        }
        private ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (offset >= bytes.Length) throw new InvalidDataException("protobuf 数值不完整。");
                var next = bytes[offset++];
                if (shift == 63 && next > 1) throw new InvalidDataException("protobuf 数值溢出。");
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new InvalidDataException("protobuf 数值溢出。");
        }
        public ulong Number() => wire == 0 ? number : throw new InvalidDataException("protobuf 数值类型无效。");
        public long Counter() => Number() <= long.MaxValue ? (long)number : throw new InvalidDataException("连接计数无效。");
        public ReadOnlySpan<byte> Data() => wire == 2 ? data : throw new InvalidDataException("protobuf 内容类型无效。");
        public string Text()
        {
            var value = Data(); if (value.Length > 65536) throw new InvalidDataException("连接文字过长。");
            return Utf8.GetString(value);
        }
    }
}

public sealed class ApplicationConnectionFeed : IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly Action<ObservedConnectionBatch> receive;
    private readonly Action<bool> connectionState;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task run;
    public Task Ready => ready.Task;
    public ApplicationConnectionFeed(int port, string secret, Action<ObservedConnectionBatch> receive, Action<bool> connectionState)
    {
        this.receive = receive; this.connectionState = connectionState;
        client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        run = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Post, "daemon.StartedService/SubscribeConnections")
                { Content = new ByteArrayContent(ConnectionWire.SubscriptionRequest()) };
                request.Content.Headers.ContentType = new("application/grpc-web+proto");
                request.Headers.Add("X-Grpc-Web", "1");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode(); timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("application/grpc-web", StringComparison.Ordinal) != true)
                    throw new InvalidDataException("应用监控接口响应无效。");
                await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
                var header = new byte[5];
                while (!lifetime.IsCancellationRequested)
                {
                    if (await stream.ReadAsync(header.AsMemory(0, 1), lifetime.Token) == 0) break;
                    await stream.ReadExactlyAsync(header.AsMemory(1), lifetime.Token);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
                    if (length > ConnectionWire.MaximumFrame || header[0] is not (0 or 128)) throw new InvalidDataException("应用监控数据帧无效。");
                    var bytes = new byte[(int)length]; await stream.ReadExactlyAsync(bytes, lifetime.Token);
                    if (header[0] == 128)
                    {
                        var trailers = Encoding.ASCII.GetString(bytes);
                        if (trailers.Contains("grpc-status:") && !trailers.Split('\n').Any(line => line.Trim().Equals("grpc-status: 0", StringComparison.Ordinal)))
                            throw new IOException("应用监控订阅被引擎拒绝。");
                        continue;
                    }
                    receive(ConnectionWire.Parse(bytes)); connectionState(true); ready.TrySetResult();
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidDataException or DecoderFallbackException or OverflowException) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
            connectionState(false);
            if (!lifetime.IsCancellationRequested)
                try { await Task.Delay(2000, lifetime.Token); } catch (OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); client.Dispose(); await run; lifetime.Dispose();
    }
}
