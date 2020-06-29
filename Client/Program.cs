using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NAudio.Wave;


namespace Client
{
    public class Program
    {
        private static readonly WaveFormat DefaultRecordingFormat = new WaveFormat(44100, 1);

        public static int SendingFrequency = 16;

        public static int SampleRate = DefaultRecordingFormat.SampleRate;
        public static int BytesPerSample = DefaultRecordingFormat.BitsPerSample * 8;

        private static readonly int SendingBufferSize = SampleRate * BytesPerSample / SendingFrequency;

        private static readonly BufferedWaveProvider BufferedWaveProvider =
            new BufferedWaveProvider(DefaultRecordingFormat);

        private static readonly Queue<byte[]> SendingQueue = new Queue<byte[]>();
        private static readonly Queue<byte[]> MainPlayQueue = new Queue<byte[]>();

        private static readonly WaveInEvent WaveIn = new WaveInEvent();
        private static readonly WaveOutEvent WaveOut = new WaveOutEvent();

        private static readonly byte[] MainSocketBuffer = new byte[SendingBufferSize];

        private static readonly Socket MainSocket =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private static bool IsSocketConnected;

        private static readonly AutoResetEvent PlayResetEvent = new AutoResetEvent(true);
        private static readonly AutoResetEvent RecordResetEvent = new AutoResetEvent(false);

        public static void Main(string[] args)
        {
            WaveOut.DesiredLatency = 1000 / SendingFrequency;
            WaveOut.Init(BufferedWaveProvider);

            WaveIn.DeviceNumber = 0;
            WaveIn.BufferMilliseconds = 1000 / SendingFrequency;
            WaveIn.WaveFormat = DefaultRecordingFormat;
            WaveIn.DataAvailable += WaveIn_DataAvailable;

            // Client
            // Console.WriteLine("Please enter host ip:port - ");
            // line = Console.ReadLine();
            // var tokens = line.Split(':');

            string host = "127.0.0.1"; //tokens[0];
            int port = 11771; //int.Parse(tokens[1]);
            var hostIp = IPAddress.Parse(host);

            var asyncResult = MainSocket.BeginConnect(hostIp, port, ar => { }, null);

            bool success = asyncResult.AsyncWaitHandle.WaitOne(5000, true);

            if (success && MainSocket.Connected)
            {
                Console.WriteLine("MainSocket Connected");

                MainSocket.EndConnect(asyncResult);
                // Пробуем подключиться в течение 5 секунд
                IsSocketConnected = true;

                MainSocket.BeginReceive(MainSocketBuffer, 0, SendingBufferSize, SocketFlags.None,
                    OnClientSocketEndReceive,
                    null);
                PlayResetEvent.Set();
            }
            else
            {
                MainSocket.Close();
                Console.WriteLine("Unable to connect");
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

                if (SendingQueue.Count > 1)
                {
                    Console.WriteLine("Sending Queue is larger than 1");
                }

                while (SendingQueue.Count > 0 && IsSocketConnected)
                {
                    var sendingBuffer = SendingQueue.Dequeue();
                    try
                    {
                        MainSocket.Send(sendingBuffer, 0, sendingBuffer.Length, SocketFlags.None);
                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("Host lost");
                        Console.WriteLine("Aborting SendThread");
                        Thread.CurrentThread.Abort();
                    }

                    // Console.WriteLine("Sent");
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

                if (MainPlayQueue.Count > 1)
                {
                    Console.WriteLine("Play buffer is larger than 1");
                }

                PlayResetEvent.WaitOne();
                while (MainPlayQueue.Count > 0)
                {
                    // Console.WriteLine("Playing");
                    var playingBuffer = MainPlayQueue.Dequeue();
                    BufferedWaveProvider.AddSamples(playingBuffer, 0, playingBuffer.Length);
                    if (WaveOut.PlaybackState != PlaybackState.Playing)
                    {
                        WaveOut.Play();
                    }
                }

                Thread.Sleep(1);
            }
        }

        private static void OnClientSocketEndReceive(IAsyncResult ar)
        {
            try
            {
                var receivedBytesCount = MainSocket.EndReceive(ar);
                Console.WriteLine("Recv data");
                byte[] playBytes = new byte[receivedBytesCount];
                Buffer.BlockCopy(MainSocketBuffer, 0, playBytes, 0, receivedBytesCount);
                MainPlayQueue.Enqueue(playBytes);
                PlayResetEvent.Set();
                MainSocket.BeginReceive(MainSocketBuffer, 0, SendingBufferSize, SocketFlags.None,
                    OnClientSocketEndReceive, null);
            }
            catch (SocketException)
            {
                Console.WriteLine("Host lost");
            }
        }

        private static void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            byte[] recordBytes = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, recordBytes, 0, e.BytesRecorded);
            SendingQueue.Enqueue(recordBytes);
        }
    }
}