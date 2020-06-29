using System.Net.Sockets;

namespace Host
{
    // объект состояния клиента
    public class ClientData
    {
        // сокет клиент
        public Socket Socket { get; private set; }

        // буфер приёма сокета
        public byte[] SocketReceiveBuffer { get; private set; }

        // буфер рассылки
        public BroadcastBuffer BroadcastBuffer { get; private set; }

        public ClientData(Socket socket, int size)
        {
            Socket = socket;
            SocketReceiveBuffer = new byte[size];
            BroadcastBuffer = new BroadcastBuffer();
        }
    }
}