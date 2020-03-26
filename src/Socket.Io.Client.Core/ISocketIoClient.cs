using System;
using System.Threading.Tasks;
using Socket.Io.Client.Core.Model;
using Socket.Io.Client.Core.Model.SocketEvent;

namespace Socket.Io.Client.Core
{
    public interface ISocketIoClient : IDisposable
    {
        SocketIoEvents Events { get; }
        bool IsRunning { get; }
        SocketIoClientOptions Options { get; }

        Task OpenAsync(Uri uri, SocketIoOpenOptions options = null);
        Task CloseAsync();

        IObservable<AckMessageEvent> Emit(string eventName);
        IObservable<AckMessageEvent> Emit(string eventName, object data);
        IObservable<AckMessageEvent> Emit(string eventName, params object[] data);

        IObservable<EventMessageEvent> On(string eventName);
    }
}