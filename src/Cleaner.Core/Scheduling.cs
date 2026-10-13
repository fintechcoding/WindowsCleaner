namespace Cleaner.Core;

public sealed class Scheduling
{
    public DateTimeOffset? Next { get; private set; }
    public bool Running { get; private set; }
    public Scheduling(bool running, DateTimeOffset now) { Running = running; Next = running ? now : null; }
    public void Start(DateTimeOffset now) { if (Running) return; Running = true; Next = now; }
    public void Stop() { Running = false; Next = null; }
    public bool Due(DateTimeOffset now) => Running && Next <= now;
    public void Completed(DateTimeOffset now) { Next = Running ? now + Product.Interval : null; }
}
