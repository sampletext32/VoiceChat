using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NAudio.Wave;

namespace Host
{
    public class Program
    {
        private static readonly WaveFormat DefaultRecordingFormat = new WaveFormat(44100, 1);

        private static readonly Socket ListenSocket =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private static readonly List<ClientData> Clients = new List<ClientData>();

        public static int SendingFrequency = 16;

        public static int SampleRate = DefaultRecordingFormat.SampleRate;
        public static int BytesPerSample = DefaultRecordingFormat.BitsPerSample * 8;

        public static int SendingBufferSize = SampleRate * BytesPerSample / SendingFrequency;

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
                if (Clients.Count == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                byte[] buffer = new byte[SendingBufferSize];
                for (int s = 0; s < SendingBufferSize; s += 2)
                {
                    // mix samples

                    float[] samples = new float[Clients.Count];
                    for (var i = 0; i < Clients.Count; i++)
                    {
                        while (Clients[i].QueuedBuffer.Queue.Count > 4) // 5 пакетов - задержка серьёзная
                        {
                            Clients[i].QueuedBuffer.Queue.Dequeue();
                        }

                        var b1 = Clients[i].QueuedBuffer.GetByte();
                        var b2 = Clients[i].QueuedBuffer.GetByte();

                        // unpack 16 bit sample
                        short sample = (short) (b1 | (b2 << 8));
                        samples[i] = (float) (sample / pow2_15);
                    }

                    // find average
                    float avg_sample = samples.Average();

                    // represent as short
                    short avg_sample_short = (short) (avg_sample * pow2_15);

                    // convert to bytes
                    byte s_b1 = (byte) (avg_sample_short & 0xff);
                    byte s_b2 = (byte) ((avg_sample_short >> 8) & 0xff);

                    //write
                    buffer[s] = s_b1;
                    buffer[s + 1] = s_b2;
                }

                for (int i = 0; i < Clients.Count; i++)
                {
                    try
                    {
                        Clients[i].Socket.Send(buffer);
                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("Client lost");
                        Clients[i].Socket.Close();
                        Clients.RemoveAt(i);
                        i--;
                    }
                }

                Console.WriteLine($"Sent {buffer.Length}");

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

                Clients.Add(clientData);
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

                Console.WriteLine($"\tRecv {receivedFromClient}");

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
                Clients.Remove(clientData);
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