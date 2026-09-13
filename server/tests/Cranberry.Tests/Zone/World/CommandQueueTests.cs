using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// The transport-to-simulation boundary: payload bytes are copied once into the queue's own arena and
// commands carry offsets, so a 20 Hz stream from 50 players allocates nothing after warm-up.
public sealed class CommandQueueTests
{
    private static Command Movement(int slot = 0) =>
        new(CommandKind.Movement, slot, EntityId.None, 0, 0, 0);

    [Fact]
    public void APayloadIsCopiedInAndReadBackByOffset()
    {
        var queue = new CommandQueue(8, 256);
        byte[] payload = [1, 2, 3, 4];

        Assert.True(queue.TryEnqueue(Movement(), payload));
        Assert.True(queue.TryDequeue(out Command command));
        Assert.Equal(payload, queue.Payload(command).ToArray());
        Assert.Equal(4, command.PayloadLength);
    }

    [Fact]
    public void TheCallerSpanIsNotAliasedAfterEnqueue()
    {
        var queue = new CommandQueue(8, 256);
        byte[] payload = [1, 2, 3, 4];
        queue.TryEnqueue(Movement(), payload);

        Array.Fill(payload, (byte)0xFF);

        Assert.True(queue.TryDequeue(out Command command));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, queue.Payload(command).ToArray());
    }

    [Fact]
    public void CommandsComeBackInArrivalOrder()
    {
        var queue = new CommandQueue(8, 256);
        for (int slot = 0; slot < 5; slot++)
        {
            queue.TryEnqueue(Movement(slot), []);
        }

        for (int slot = 0; slot < 5; slot++)
        {
            Assert.True(queue.TryDequeue(out Command command));
            Assert.Equal(slot, command.Slot);
        }

        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void AFullRingDropsRatherThanGrows()
    {
        var queue = new CommandQueue(2, 256);

        Assert.True(queue.TryEnqueue(Movement(), []));
        Assert.True(queue.TryEnqueue(Movement(), []));
        Assert.False(queue.TryEnqueue(Movement(), []));
        Assert.Equal(1, queue.Dropped);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void AFullArenaDropsRatherThanGrows()
    {
        var queue = new CommandQueue(8, 8);

        Assert.True(queue.TryEnqueue(Movement(), new byte[8]));
        Assert.False(queue.TryEnqueue(Movement(), new byte[1]));
        Assert.Equal(1, queue.Dropped);
    }

    [Fact]
    public void TheArenaIsOnlyResetWhenTheRingIsEmpty()
    {
        var queue = new CommandQueue(8, 16);
        queue.TryEnqueue(Movement(), [1, 2, 3, 4]);
        queue.TryEnqueue(Movement(), [5, 6, 7, 8]);

        // A budget-clipped drain leaves commands whose payloads still point into the arena.
        Assert.True(queue.TryDequeue(out _));
        Assert.False(queue.ResetArena());

        Assert.True(queue.TryDequeue(out Command second));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, queue.Payload(second).ToArray());
        Assert.True(queue.ResetArena());
    }

    [Fact]
    public void TheArenaIsReusedTickAfterTick()
    {
        var queue = new CommandQueue(8, 16);
        for (int tick = 0; tick < 100; tick++)
        {
            Assert.True(queue.TryEnqueue(Movement(), [(byte)tick, 2, 3, 4]));
            Assert.True(queue.TryDequeue(out Command command));
            Assert.Equal((byte)tick, queue.Payload(command)[0]);
            Assert.True(queue.ResetArena());
        }

        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public void EnqueueAndDequeueAreFreeOfManagedAllocation()
    {
        var queue = new CommandQueue(64, 4096);
        byte[] payload = new byte[32];
        queue.TryEnqueue(Movement(), payload);
        queue.TryDequeue(out _);
        queue.ResetArena();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            queue.TryEnqueue(Movement(), payload);
            queue.TryDequeue(out Command command);
            _ = queue.Payload(command).Length;
            queue.ResetArena();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ClearDropsEverythingIncludingTheArena()
    {
        var queue = new CommandQueue(8, 16);
        queue.TryEnqueue(Movement(), [1, 2, 3, 4]);
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));
        Assert.True(queue.TryEnqueue(Movement(), new byte[16]));
    }
}
