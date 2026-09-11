using System.Net.Sockets;
using System.Security.Authentication;
using StorageHub.Agent.Windows;

namespace StorageHub.Agent.Windows.Tests;

public sealed class ConnectionOpenFailureClassifierTests
{
    [Fact]
    public void Treats_socket_timeout_and_io_failures_as_connectivity()
    {
        Assert.True(ConnectionOpenFailureClassifier.IsConnectivityFailure(
            new SocketException((int)SocketError.HostUnreachable)));
        Assert.True(ConnectionOpenFailureClassifier.IsConnectivityFailure(new TimeoutException()));
        Assert.True(ConnectionOpenFailureClassifier.IsConnectivityFailure(new IOException("reset")));
    }

    [Fact]
    public void Unwraps_nested_connectivity_failures()
    {
        var wrapped = new InvalidOperationException(
            "The provider failed.",
            new IOException("broken pipe", new SocketException((int)SocketError.NetworkDown)));

        Assert.True(ConnectionOpenFailureClassifier.IsConnectivityFailure(wrapped));
    }

    [Fact]
    public void Keeps_authentication_failures_terminal()
    {
        // A rejected credential must not be retried as though the network were down, even though
        // it arrives through the same socket stack.
        Assert.False(ConnectionOpenFailureClassifier.IsConnectivityFailure(
            new AuthenticationException("bad credentials")));
        Assert.False(ConnectionOpenFailureClassifier.IsConnectivityFailure(
            new InvalidOperationException("outer", new AuthenticationException("bad credentials"))));
    }

    [Fact]
    public void Keeps_programming_faults_terminal()
    {
        Assert.False(ConnectionOpenFailureClassifier.IsConnectivityFailure(new InvalidOperationException()));
        Assert.False(ConnectionOpenFailureClassifier.IsConnectivityFailure(new ArgumentNullException("path")));
    }

    [Fact]
    public void Rejects_a_null_exception()
    {
        _ = Assert.Throws<ArgumentNullException>(
            () => ConnectionOpenFailureClassifier.IsConnectivityFailure(null!));
    }
}
