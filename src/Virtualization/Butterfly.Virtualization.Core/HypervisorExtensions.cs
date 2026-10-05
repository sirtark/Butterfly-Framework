namespace Butterfly.Virtualization
{
    public static class HypervisorExtensions
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        /// <exception cref="VirtualMachineNotFoundException">The machine does not exist.</exception>
        public static async Task<VirtualMachine> GetAsync(this IHypervisor hypervisor, string name, CancellationToken cancellationToken = default) =>
            await hypervisor.FindAsync(name, cancellationToken).ConfigureAwait(false) ?? throw new VirtualMachineNotFoundException(name);

        /// <summary>Polls the machine until it reaches <paramref name="state"/>.</summary>
        /// <exception cref="TimeoutException">The machine did not reach the state in time.</exception>
        public static async Task<VirtualMachine> WaitForStateAsync(this IHypervisor hypervisor, string name, VirtualMachineState state, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    var machine = await hypervisor.GetAsync(name, timeoutSource.Token).ConfigureAwait(false);
                    if (machine.State == state)
                        return machine;
                    await Task.Delay(PollInterval, timeoutSource.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The virtual machine '{name}' did not reach the {state} state within {timeout}.");
            }
        }

        /// <summary>Shuts the guest down and, if it is not off within <paramref name="timeout"/>, turns it off.</summary>
        public static async Task StopAsync(this IHypervisor hypervisor, string name, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var machine = await hypervisor.GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (machine.State == VirtualMachineState.Off)
                return;

            if (machine.State == VirtualMachineState.Running)
            {
                await hypervisor.ShutdownAsync(name, cancellationToken).ConfigureAwait(false);
                try
                {
                    await hypervisor.WaitForStateAsync(name, VirtualMachineState.Off, timeout, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException)
                {
                    // The guest ignored the request (no ACPI/integration services, or it is hung).
                }
            }

            await hypervisor.TurnOffAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }
}
