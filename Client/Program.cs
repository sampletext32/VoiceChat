﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio;
using NAudio.Wave;


namespace Client
{
    class Program
    {
        private static readonly WaveFormat DefaultRecordingFormat = new WaveFormat(44100, 1);

        private static readonly Queue<byte[]> SendingQueue = new Queue<byte[]>();
        private static readonly Queue<byte[]> MainPlayQueue = new Queue<byte[]>();

        private static readonly WaveInEvent WaveIn = new WaveInEvent();
        private static readonly WaveOutEvent WaveOut = new WaveOutEvent();

        private static readonly byte[] MainSocketBuffer = new byte[32768];

        private static readonly Socket MainSocket =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private static bool IsSocketConnected;

        private static readonly AutoResetEvent PlayResetEvent = new AutoResetEvent(true);
        private static readonly AutoResetEvent RecordResetEvent = new AutoResetEvent(false);

        private static readonly List<ClientData> ConnectedSocketsData = new List<ClientData>();
        
        public static void Main(string[] args)
        {
            WaveOut.PlaybackStopped += WaveOut_PlaybackStopped;

            WaveIn.DeviceNumber = 0;
            WaveIn.WaveFormat = DefaultRecordingFormat;
            WaveIn.DataAvailable += WaveIn_DataAvailable;


            var line = Console.ReadLine();
            if (line == "1")
            {
                // Client
                // Console.WriteLine("Please enter host ip:port - ");
                // line = Console.ReadLine();
                // var tokens = line.Split(':');

                string host = "127.0.0.1"; //tokens[0];
                int port = 11771; //int.Parse(tokens[1]);
                var hostIp = IPAddress.Parse(host);

                var asyncResult = MainSocket.BeginConnect(hostIp, port, ar => { }, null);

                bool success = asyncResult.AsyncWaitHandle.WaitOne(5000, true);

                if (success)
                {
                    if (MainSocket.Connected)
                    {
                        Console.WriteLine("MainSocket Connected");

                        MainSocket.EndConnect(asyncResult);
                        // Пробуем подключиться в течение 5 секунд
                        IsSocketConnected = true;

                        MainSocket.BeginReceive(MainSocketBuffer, 0, 32768, SocketFlags.None, OnClientSocketEndReceive,
                            null);
                        PlayResetEvent.Set();
                    }
                    else
                    {
                        Console.WriteLine("MainSocket Is Not Connected");
                    }
                }
                else
                {
                    MainSocket.Close();
                    Console.WriteLine("Unable to connect");
                }
            }
            else if (line == "2")
            {
                MainSocket.BeginAccept(OnHostAcceptClient, null);
            }

            // Launch Record Thread
            Thread recordThread = new Thread(RecordThreadJob);
            recordThread.Start();
            RecordResetEvent.Set();

            Thread playThread = new Thread(PlayThreadJob);
            playThread.Start();

            Thread sendThread = new Thread(SendThreadJob);
            sendThread.Start();

            Console.ReadKey();
            RecordResetEvent.Set();
            playThread.Abort();
            sendThread.Abort();
        }

        private static void OnHostAcceptClient(IAsyncResult ar)
        {
            Socket client = MainSocket.EndAccept(ar);
            ClientData clientData = new ClientData(client, 8192);
            client.BeginReceive(clientData.Buffer, clientData.ReceivedBytes,
                clientData.Buffer.Length, SocketFlags.None, OnHostReceiveFromClient, clientData);
            ConnectedSocketsData.Add(clientData);
        }

        private static void OnHostReceiveFromClient(IAsyncResult ar)
        {
            var clientData = (ClientData) ar.AsyncState;
            var received = clientData.Socket.EndReceive(ar);

            byte[] playBytes = new byte[received];
            Buffer.BlockCopy(MainSocketBuffer, 0, playBytes, 0, received);

            clientData.PlayQueue.Enqueue(playBytes);

            clientData.Socket.BeginReceive(clientData.Buffer, clientData.ReceivedBytes,
                clientData.Buffer.Length, SocketFlags.None, OnHostReceiveFromClient, clientData);
        }

        private static void RecordThreadJob()
        {
            RecordResetEvent.WaitOne();
            WaveIn.StartRecording();

            RecordResetEvent.WaitOne();
            WaveIn.StopRecording();
        }

        private static void SendThreadJob()
        {
            while (Thread.CurrentThread.ThreadState != ThreadState.AbortRequested)
            {
                if (SendingQueue.Count > 10)
                {
                    Console.WriteLine($"Sending queue is too large: {SendingQueue.Count}, trimming to 10 elems");
                    while (SendingQueue.Count > 10)
                    {
                        // Delete old data, no need to send
                        SendingQueue.Dequeue();
                    }
                }

                while (SendingQueue.Count != 0 && IsSocketConnected)
                {
                    var sendingBuffer = SendingQueue.Dequeue();
                    MainSocket.Send(sendingBuffer, 0, sendingBuffer.Length, SocketFlags.None);
                }

                Thread.Sleep(1);
            }
        }

        private static void PlayThreadJob()
        {
            while (Thread.CurrentThread.ThreadState != ThreadState.AbortRequested)
            {
                if (MainPlayQueue.Count > 10)
                {
                    Console.WriteLine($"Playing Queue is too large: {MainPlayQueue.Count}, trimming to 10 elems");
                    while (MainPlayQueue.Count > 10)
                    {
                        // Delete old data, no need to play
                        MainPlayQueue.Dequeue();
                    }
                }

                while (MainPlayQueue.Count != 0)
                {
                    var playingBuffer = MainPlayQueue.Dequeue();
                    PlayResetEvent.WaitOne();
                    IWaveProvider provider =
                        new RawSourceWaveStream(new MemoryStream(playingBuffer), DefaultRecordingFormat);
                    WaveOut.Init(provider);
                    WaveOut.Play();
                }

                Thread.Sleep(1);
            }
        }

        private static void OnClientSocketEndReceive(IAsyncResult ar)
        {
            var receivedBytesCount = MainSocket.EndReceive(ar);
            byte[] playBytes = new byte[receivedBytesCount];
            Buffer.BlockCopy(MainSocketBuffer, 0, playBytes, 0, receivedBytesCount);
            MainPlayQueue.Enqueue(playBytes);
            MainSocket.BeginReceive(MainSocketBuffer, 0, 32768, SocketFlags.None, OnClientSocketEndReceive, null);
        }

        private static void WaveOut_PlaybackStopped(object sender, StoppedEventArgs e)
        {
            PlayResetEvent.Set();
        }

        private static void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            byte[] recordBytes = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, recordBytes, 0, e.BytesRecorded);
            SendingQueue.Enqueue(recordBytes);
        }
    }

    public class ClientData
    {
        public int ReceivedBytes { get; set; }
        public Socket Socket { get; private set; }
        public byte[] Buffer { get; private set; }
        public Queue<byte[]> PlayQueue { get; private set; }

        public ClientData(Socket socket, int size)
        {
            Socket = socket;
            Buffer = new byte[size];
            PlayQueue = new Queue<byte[]>();
        }
    }
}