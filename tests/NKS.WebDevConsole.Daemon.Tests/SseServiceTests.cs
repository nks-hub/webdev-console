using NKS.WebDevConsole.Daemon.Services;
using Microsoft.AspNetCore.Http;

namespace NKS.WebDevConsole.Daemon.Tests;

public class SseServiceTests
{
    private readonly SseService _service = new();

    private static HttpResponse CreateMockResponse()
    {
        var context = new DefaultHttpContext();
        return context.Response;
    }

    [Fact]
    public void NewService_HasZeroClients()
    {
        Assert.Equal(0, _service.ClientCount);
    }

    [Fact]
    public void AddClient_IncrementsCount()
    {
        _service.AddClient(CreateMockResponse());
        Assert.Equal(1, _service.ClientCount);
    }

    [Fact]
    public void AddClient_ReturnsNonEmptyId()
    {
        var id = _service.AddClient(CreateMockResponse());
        Assert.False(string.IsNullOrEmpty(id));
    }

    [Fact]
    public void AddClient_ReturnsUniqueIds()
    {
        var id1 = _service.AddClient(CreateMockResponse());
        var id2 = _service.AddClient(CreateMockResponse());
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void RemoveClient_DecrementsCount()
    {
        var id = _service.AddClient(CreateMockResponse());
        Assert.Equal(1, _service.ClientCount);

        _service.RemoveClient(id);
        Assert.Equal(0, _service.ClientCount);
    }

    [Fact]
    public void RemoveClient_UnknownId_DoesNotThrow()
    {
        _service.RemoveClient("nonexistent-id");
        Assert.Equal(0, _service.ClientCount);
    }

    [Fact]
    public void MultipleClients_CountIsCorrect()
    {
        _service.AddClient(CreateMockResponse());
        _service.AddClient(CreateMockResponse());
        _service.AddClient(CreateMockResponse());

        Assert.Equal(3, _service.ClientCount);
    }

    [Fact]
    public async Task BroadcastAsync_NoClients_DoesNotThrow()
    {
        await _service.BroadcastAsync("test-event", new { message = "hello" });
    }

    [Fact]
    public void AddAndRemove_SameClient_ReturnsToZero()
    {
        var id = _service.AddClient(CreateMockResponse());
        Assert.Equal(1, _service.ClientCount);
        _service.RemoveClient(id);
        Assert.Equal(0, _service.ClientCount);
    }

    [Fact]
    public void RemoveClient_CalledTwice_DoesNotThrow()
    {
        var id = _service.AddClient(CreateMockResponse());
        _service.RemoveClient(id);
        _service.RemoveClient(id);
        Assert.Equal(0, _service.ClientCount);
    }

    [Fact]
    public void AddClient_MultipleThenRemoveOne_CountCorrect()
    {
        var id1 = _service.AddClient(CreateMockResponse());
        var id2 = _service.AddClient(CreateMockResponse());
        var id3 = _service.AddClient(CreateMockResponse());
        Assert.Equal(3, _service.ClientCount);
        _service.RemoveClient(id2);
        Assert.Equal(2, _service.ClientCount);
    }
}
