using System.Runtime.InteropServices;
namespace MouseWithoutBorders;
internal sealed class ClipboardChangeTracker
{
    private readonly object gate = new();
    private readonly System.Collections.Generic.Queue<uint> remote = new();
    private uint sent;
    internal void MarkRemote(uint sequence) { lock (gate) { remote.Enqueue(sequence); while (remote.Count > 16) remote.Dequeue(); } }
    internal bool TryLocal(uint sequence)
    {
        lock (gate)
        {
            if (sequence != 0 && (remote.Contains(sequence) || sequence == sent)) return false;
            sent = sequence;
            return true;
        }
    }
}
internal static class ClipboardSequence
{
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
}
