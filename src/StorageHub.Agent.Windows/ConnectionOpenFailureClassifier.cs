using System.Net.Sockets;
using System.Security.Authentication;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Classifies an exception thrown while opening a provider connection.
/// </summary>
/// <remarks>
/// Connectivity loss is the expected condition on a laptop that sleeps, roams, or drops off
/// Wi-Fi, and it is temporary by nature. Reporting it as a non-transient failure retires a
/// queued transfer or sync permanently, so a connectivity-class exception must be marked
/// transient and left for the caller's retry policy. A genuine protocol, credential, or
/// programming fault stays terminal so it is not retried forever.
/// </remarks>
internal static class ConnectionOpenFailureClassifier
{
    /// <summary>
    /// Returns true when the failure reflects the network being unreachable rather than the
    /// request being invalid.
    /// </summary>
    internal static bool IsConnectivityFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        for (var current = error; current is not null; current = current.InnerException)
        {
            // An authentication failure is a credential/trust problem, not connectivity, even
            // though it surfaces from the same socket stack.
            if (current is AuthenticationException)
            {
                return false;
            }

            if (current is SocketException or TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }
}
