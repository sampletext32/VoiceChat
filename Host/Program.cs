using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Host
{
    public class Program
    {
        public static int RecordingSampleRate = 44100; // частота дискретизации записи
        public static int RecordingChannels = 1; // количество каналов записи
        public static int RecordingBytesPerSample = RecordingChannels * 2; // 16 бит формат по умолчанию

        public static int SendingFrequency = 16; // частота рассылки сервера
        public static int SendingBufferSize = RecordingSampleRate * RecordingBytesPerSample / SendingFrequency;

        public static int MaxBufferedPackets = 5; // максимальное количество пакетов в очереди на отправку

        // Главный сокет
        private static readonly Socket ListenSocket =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private static readonly List<ClientData> ConnectedClients = new List<ClientData>();

        public static void Main(string[] args)
        {
            ListenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 11771));
            ListenSocket.Listen(10);
            ListenSocket.BeginAccept(OnAcceptClient, null);

            Console.WriteLine("Server started");

            Thread broadcastThread = new Thread(BroadcastThreadJob);
            broadcastThread.Start();

            Console.ReadKey();

            broadcastThread.Abort();
        }

        private static void BroadcastThreadJob()
        {
            const double pow2_15 = 1 << 15;
            while (Thread.CurrentThread.ThreadState != ThreadState.AbortRequested)
            {
                // если клиентов нет - просто ждём
                if (ConnectedClients.Count == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                byte[] finalSendingBuffer = new byte[SendingBufferSize];
                for (int b = 0; b < SendingBufferSize; b += 2)
                {
                    // mix samples

                    float[] samplesFromClients = new float[ConnectedClients.Count];
                    for (int i = 0; i < ConnectedClients.Count; i++)
                    {
                        // освобождаем очередь пакетов
                        while (ConnectedClients[i].QueuedBuffer.Queue.Count >= MaxBufferedPackets)
                        {
                            ConnectedClients[i].QueuedBuffer.Queue.Dequeue();
                        }

                        // вытаскиваем 2 байта из очереди от клиента
                        var firstByte = ConnectedClients[i].QueuedBuffer.GetByte();
                        var secondByte = ConnectedClients[i].QueuedBuffer.GetByte();

                        // распаковываем PCM сэмпл
                        short sample = (short) (firstByte | (secondByte << 8));

                        //записываем сэмпл от клиента
                        samplesFromClients[i] = (float) (sample / pow2_15); 
                    }

                    // ищем максимальное значение сэмпла
                    float maxSample = samplesFromClients.Max();

                    // represent as short
                    short maxSampleShort = (short) (maxSample * pow2_15);

                    // convert to bytes
                    byte sendingFirstByte = (byte) (maxSampleShort & 0xff);
                    byte sendingSecondByte = (byte) ((maxSampleShort >> 8) & 0xff);

                    //write
                    finalSendingBuffer[b] = sendingFirstByte;
                    finalSendingBuffer[b + 1] = sendingSecondByte;
                }

                for (int i = 0; i < ConnectedClients.Count; i++)
                {
                    try
                    {
                        ConnectedClients[i].Socket.Send(finalSendingBuffer);
                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("Client lost");
                        ConnectedClients[i].Socket.Close();
                        ConnectedClients.RemoveAt(i);
                        i--;
                    }
                }

                // Console.WriteLine($"Sent {finalSendingBuffer.Length}");

                Thread.Sleep(1000 / SendingFrequency);
            }
        }

        private static void OnAcceptClient(IAsyncResult ar)
        {
            try
            {
                Socket clientSocket = ListenSocket.EndAccept(ar);
                Console.WriteLine("Client connected");

                ClientData clientData = new ClientData(clientSocket, SendingBufferSize);

                ConnectedClients.Add(clientData);
                clientSocket.BeginReceive(clientData.SocketReceiveBuffer, 0, clientData.SocketReceiveBuffer.Length,
                    SocketFlags.None,
                    OnReceiveFromClient, clientData);
            }
            catch (SocketException)
            {
                Console.WriteLine("Exception in Accept");
            }

            ListenSocket.BeginAccept(OnAcceptClient, null);
        }

        private static void OnReceiveFromClient(IAsyncResult ar)
        {
            var clientData = (ClientData) ar.AsyncState;
            try
            {
                var receivedFromClient = clientData.Socket.EndReceive(ar);

                // Console.WriteLine($"\tRecv {receivedFromClient}");

                byte[] bufferForUserData = new byte[receivedFromClient];
                Buffer.BlockCopy(clientData.SocketReceiveBuffer, 0, bufferForUserData, 0, receivedFromClient);

                clientData.QueuedBuffer.Queue.Enqueue(bufferForUserData);

                clientData.Socket.BeginReceive(clientData.SocketReceiveBuffer, 0,
                    clientData.SocketReceiveBuffer.Length, SocketFlags.None, OnReceiveFromClient, clientData);
            }
            catch (SocketException)
            {
                Console.WriteLine("Client disconnected");
                clientData.Socket.Close();
                ConnectedClients.Remove(clientData);
            }
        }
    }

    public class ClientData
    {
        public Socket Socket { get; private set; }
        public byte[] SocketReceiveBuffer { get; private set; }
        public ReceiveBuffer QueuedBuffer { get; private set; }

        public ClientData(Socket socket, int size)
        {
            Socket = socket;
            SocketReceiveBuffer = new byte[size];
            QueuedBuffer = new ReceiveBuffer();
        }
    }

    public class ReceiveBuffer
    {
        public byte[] CurrentBuffer { get; private set; }
        public int _position { get; private set; }
        public Queue<byte[]> Queue { get; private set; }

        public byte GetByte()
        {
            if (CurrentBuffer == null || _position == CurrentBuffer.Length)
            {
                if (Queue.Count > 0)
                {
                    CurrentBuffer = Queue.Dequeue();
                    _position = 0;
                    return GetByte();
                }
                else
                {
                    CurrentBuffer = null;
                    return 0;
                }
            }
            else
            {
                byte data = CurrentBuffer[_position];
                _position++;
                return data;
            }
        }

        public ReceiveBuffer()
        {
            Queue = new Queue<byte[]>();
        }
    }
}