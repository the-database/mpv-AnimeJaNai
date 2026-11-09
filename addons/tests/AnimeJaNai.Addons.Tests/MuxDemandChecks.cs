using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task MuxDemandChecks()
    {
        await Test("Native stream demand resumes after consuming a six-second segment", async () =>
        {
            var gate = new MuxDemandGate(100);
            gate.Admit(0, 15, default);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task next = Task.Run(() => gate.Admit(15, 16, stop.Token));
            await Until(() => gate.Status()["bufferPaused"]!.GetValue<bool>());
            True(!next.IsCompleted && gate.Status()["producedEndSeconds"]!.GetValue<double>() == 115);
            gate.SetDemand(106); await next.WaitAsync(TimeSpan.FromSeconds(2));
            True(gate.Status()["producedEndSeconds"]!.GetValue<double>() == 116 && !gate.Status()["bufferPaused"]!.GetValue<bool>());
        });
        await Test("Native stream user pause and cancellation remain independent of buffering", async () =>
        {
            var gate = new MuxDemandGate(0); gate.Pause(true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task packet = Task.Run(() => gate.Admit(0, 1, stop.Token));
            await Task.Delay(30); True(!packet.IsCompleted);
            gate.Pause(false); await packet.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Pause(true); Task cancelled = Task.Run(() => gate.Admit(1, 2, stop.Token)); stop.Cancel();
            try { await cancelled; throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
            True(gate.Status()["producedEndSeconds"]!.GetValue<double>() == 1);
        });
    }
}
