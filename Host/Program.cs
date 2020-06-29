﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Host
{
    class Program
    {
        private static readonly Socket MainSocket =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private static readonly List<ClientData> Clients = new List<ClientData>();

        public static int SendingBufferSize = 8820;

        public static void Main(string[] args)
        {
            MainSocket.Bind(new IPEndPoint(IPAddress.Loopback, 11771));
            MainSocket.Listen(10);
            MainSocket.BeginAccept(OnAcceptClient, null);

            Console.WriteLine("Server started");

            Thread sendThread = new Thread(SendThreadJob);
            sendThread.Start();

            Console.ReadKey();

            sendThread.Abort();
        }

        private static void SendThreadJob()
        {
            double pow2_15 = 1 << 15;
            while (Thread.CurrentThread.ThreadState != ThreadState.AbortRequested)
            {
                byte[] buffer = new byte[SendingBufferSize];
                if (Clients.Count != 0)
                {
                    for (int s = 0; s < buffer.Length / 2; s += 2)
                    {
                        // mix samples

                        float[] samples = new float[Clients.Count];
                        for (var i = 0; i < Clients.Count; i++)
                        {
                            var b1 = Clients[i].ReceiveBuffer.GetByte();
                            var b2 = Clients[i].ReceiveBuffer.GetByte();

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
                }

                for (int i = 0; i < Clients.Count; i++)
                {
                    Clients[i].Socket.Send(buffer);
                }

                Console.WriteLine($"Sent {buffer.Length}");

                Thread.Sleep((int) ((float) SendingBufferSize / 44100 * 1000));
            }
        }

        private static void OnAcceptClient(IAsyncResult ar)
        {
            Socket client = MainSocket.EndAccept(ar);
            Console.WriteLine("Client connected");

            ClientData clientData = new ClientData(client, SendingBufferSize);

            Clients.Add(clientData);
            client.BeginReceive(clientData.ReceiveSocketBuffer, 0, clientData.ReceiveSocketBuffer.Length,
                SocketFlags.None,
                OnReceiveFromClient, clientData);

            MainSocket.BeginAccept(OnAcceptClient, null);
        }

        private static void OnReceiveFromClient(IAsyncResult ar)
        {
            var clientData = (ClientData) ar.AsyncState;
            try
            {
                var received = clientData.Socket.EndReceive(ar);

                Console.WriteLine($"Received {received}");

                byte[] actualBytes = new byte[received];
                Buffer.BlockCopy(clientData.ReceiveSocketBuffer, 0, actualBytes, 0, received);

                clientData.ReceiveBuffer.Queue.Enqueue(actualBytes);

                clientData.Socket.BeginReceive(clientData.ReceiveSocketBuffer, 0,
                    clientData.ReceiveSocketBuffer.Length, SocketFlags.None, OnReceiveFromClient, clientData);
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
        public byte[] ReceiveSocketBuffer { get; private set; }
        public ReceiveBuffer ReceiveBuffer { get; set; }

        public ClientData(Socket socket, int size)
        {
            Socket = socket;
            ReceiveSocketBuffer = new byte[size];
            ReceiveBuffer = new ReceiveBuffer();
        }
    }

    public class ReceiveBuffer
    {
        private byte[] _currentBuffer { get; set; }
        private int _position = 0;
        public Queue<byte[]> Queue { get; set; }

        public byte GetByte()
        {
            if (_currentBuffer == null || _position == _currentBuffer.Length)
            {
                if (Queue.Count > 0)
                {
                    _currentBuffer = Queue.Dequeue();
                    _position = 0;
                    return GetByte();
                }
                else
                {
                    return 0;
                }
            }
            else
            {
                byte data = _currentBuffer[_position];
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