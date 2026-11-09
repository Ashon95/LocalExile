using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// A C→S message's length comes from the client's own writer (Agents/docs/protocol/c2s.md): SplitClientMessages frames by
// it and stops at the first id it has no length for, discarding the rest of that read. These are ids the Gateway has no
// constant for, so they frame only through the generated table (ClientMessageLengths).
public class ClientMessageLengthsTests
{
    static byte[] Message(ushort id, int length, byte fill = 0xAA)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)(id >> 8);
        bytes[1] = (byte)id;
        for (int i = 2; i < length; i++) bytes[i] = fill;
        return bytes;
    }

    // 0x002b (21), 0x004b (6) and 0x0057 (11) arrive in one read with a 0x1b tick (7, u8 counter + two u32): before the
    // table the read stopped at 0x002b and lost the other three messages.
    [Fact]
    public void Coalesced_ids_without_a_constant_each_frame_in_one_read()
    {
        byte[] read =
        [
            .. Message(0x002b, 21),
            .. Message(0x004b, 6),
            .. Message(0x001b, 7, 0x00),
            .. Message(0x0057, 11),
        ];

        var messages = InstanceProtocol.SplitClientMessages(read, out byte[] partial, out byte[] unknown);

        Assert.Empty(unknown);
        Assert.Empty(partial);
        Assert.Equal(new ushort[] { 0x002b, 0x004b, 0x001b, 0x0057 }, messages.Select(m => m.Id));
        Assert.Equal(new[] { 21, 6, 7, 11 }, messages.Select(m => m.Bytes.Length));
        Assert.Equal(read, messages.SelectMany(m => m.Bytes).ToArray());
    }

    // A table length waits for the rest of the message like any other, and an id with no exact writer still reports the
    // rest of the read as unframed so the connection's lost-stream guard keeps working.
    [Fact]
    public void A_known_length_waits_for_the_rest_and_an_unknown_id_is_still_unframed()
    {
        byte[] rest = Message(0x0057, 11);

        Assert.Empty(InstanceProtocol.SplitClientMessages(rest.AsSpan(0, 5), out byte[] partial, out byte[] unknown));
        Assert.Equal(rest.AsSpan(0, 5).ToArray(), partial);
        Assert.Empty(unknown);

        var framed = InstanceProtocol.SplitClientMessages([.. partial, .. rest.AsSpan(5)], out partial, out unknown);
        Assert.Single(framed);
        Assert.Equal((0x0057, 11), (framed[0].Id, framed[0].Bytes.Length));
        Assert.Empty(partial);

        // 0x7fff has no writer at all in c2s.json, so nothing after it can be framed.
        byte[] lost = [.. Message(0x7fff, 4), .. rest];
        Assert.Empty(InstanceProtocol.SplitClientMessages(lost, out _, out unknown));
        Assert.Equal(lost, unknown);
    }
}
