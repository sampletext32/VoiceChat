﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NAudio.Wave;


namespace Client
{
    public class Program
    {
        // частота дискретизации записи
        public static int RecordingSampleRate = 44100;

        // количество каналов записи
        public static int RecordingChannels = 1;

        // 16 бит формат по умолчанию
        public static int RecordingBytesPerSample = RecordingChannels * 2;

        // формат записи
        private static readonly WaveFormat RecordingFormat =
            new WaveFormat(RecordingSampleRate, RecordingChannels);

        // частота отправки клиента
        public static int SendingFrequency = 16;

        // размер отправляемого буфера
        private static readonly int SendingBufferSize =
            RecordingSampleRate * RecordingBytesPerSample / SendingFrequency;

        // максимальное количество пакетов в очереди на отправку
        public static int MaxBufferedSendingPackets = 5;

        // максимальное количество пакетов в очереди на воспроизведение
        public static int MaxBufferedPlayingPackets = 5;

        // буферизированный поток сэмплов для воспроизведения
        private static readonly BufferedWaveProvider BufferedWaveProvider =
            new BufferedWaveProvider(RecordingFormat);

        // очередь пакетов на отправку
        private static readonly Queue<byte[]> SendingQueue = new Queue<byte[]>();

        // очередь пакетов на воспроизведение
        private static readonly Queue<byte[]> MainPlayQueue = new Queue<byte[]>();

        // входной поток с микрофона
        private static readonly WaveInEvent WaveIn = new WaveInEvent();

        // выходной поток на динамики
        private static readonly WaveOutEvent WaveOut = new WaveOutEvent();

        // буфер главного сокета
        private static readonly byte[] MainSocketBuffer = new byte[SendingBufferSize];

        // главный сокет
        private static readonly Socket MainSocket =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // подключен ли сейчас сокет
        private static bool IsSocketConnected;

        // событие синхронизации потоков для воспроизведения
        private static readonly AutoResetEvent PlayResetEvent = new AutoResetEvent(true);

        // событие синхронизации потоков для записи
        private static readonly AutoResetEvent RecordResetEvent = new AutoResetEvent(false);

        public static void Main(string[] args)
        {
            // установка вывода и ввода звука
            WaveOut.DesiredLatency = 1000 / SendingFrequency;
            WaveOut.Init(BufferedWaveProvider);

            WaveIn.DeviceNumber = 0;
            WaveIn.BufferMilliseconds = 1000 / SendingFrequency;
            WaveIn.WaveFormat = RecordingFormat;
            WaveIn.DataAvailable += WaveIn_DataAvailable;

            // установка подключения

            string host; // хост подключения
            int port; // порт подключения

            if (true)
            {
                host = "127.0.0.1";
                port = 11771;
            }
            else
            {
                Console.WriteLine("Please enter host ip:port - ");
                string line = Console.ReadLine();
                var tokens = line.Split(':');
                host = tokens[0];
                port = int.Parse(tokens[1]);
            }

            var hostIpAddress = IPAddress.Parse(host);

            var connectResult = MainSocket.BeginConnect(hostIpAddress, port, ar => { }, null);

            // Пробуем подключиться в течение 5 секунд
            bool connectFinished = connectResult.AsyncWaitHandle.WaitOne(5000, true);

            // если получилось подключиться
            if (connectFinished && MainSocket.Connected)
            {
                MainSocket.EndConnect(connectResult);
                Console.WriteLine("MainSocket Connected");

                IsSocketConnected = true;

                // начинаем приём от сервера
                MainSocket.BeginReceive(MainSocketBuffer, 0, SendingBufferSize, SocketFlags.None,
                    OnSocketEndReceive,
                    null);

                // запускаем воспроизведение
                PlayResetEvent.Set();
            }
            else
            {
                // если не получилось подключиться
                MainSocket.Close();
                Console.WriteLine("Unable to connect");
            }

            if (IsSocketConnected)
            {
                // запускаем потоки

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
            else
            {
                Console.ReadKey();
            }
        }

        // работа потока записи
        private static void RecordThreadJob()
        {
            // ожидаем разрешения начала записи и начинаем
            RecordResetEvent.WaitOne();
            WaveIn.StartRecording();

            // ждём пока необходимо остановить запись и останавливаем
            RecordResetEvent.WaitOne();
            WaveIn.StopRecording();
        }

        // работа потока отправки
        private static void SendThreadJob()
        {
            // пока не требуется завершить поток
            while (Thread.CurrentThread.ThreadState != ThreadState.AbortRequested)
            {
                // если в отправке слишком много пакетов, удаляем самые старые, они уже не нужны
                if (SendingQueue.Count >= MaxBufferedSendingPackets)
                {
                    Console.WriteLine(
                        $"Sending queue is too large: {SendingQueue.Count}, trimming to {MaxBufferedSendingPackets} elems");
                    while (SendingQueue.Count > MaxBufferedSendingPackets)
                    {
                        SendingQueue.Dequeue();
                    }
                }

                // пока есть данные к отправке и сокет подключен
                while (SendingQueue.Count > 0 && IsSocketConnected)
                {
                    // получаем первый буфер
                    var sendingBuffer = SendingQueue.Dequeue();
                    try
                    {
                        // пытаемся отправить буфер
                        MainSocket.Send(sendingBuffer, 0, sendingBuffer.Length, SocketFlags.None);
                    }
                    catch (SocketException)
                    {
                        // если словили ошибку - сокет отключился
                        Console.WriteLine("Host lost");
                        Console.WriteLine("Aborting SendThread");
                        IsSocketConnected = false;
                        Thread.CurrentThread.Abort();
                    }

                    // Console.WriteLine("Sent");
                }

                // ожидаем 1 миллисекунду, чтобы поток бился в припадке скорости
                Thread.Sleep(1);
            }
        }

        // работа потока воспроизведения
        private static void PlayThreadJob()
        {
            // пока не требуется остановить поток
            while (Thread.CurrentThread.ThreadState != ThreadState.AbortRequested)
            {
                // если в воспроизведении слишком много пакетов, удаляем самые старые, они уже не нужны
                if (MainPlayQueue.Count > MaxBufferedPlayingPackets)
                {
                    Console.WriteLine(
                        $"Playing Queue is too large: {MainPlayQueue.Count}, trimming to {MaxBufferedPlayingPackets} elems");
                    while (MainPlayQueue.Count > MaxBufferedPlayingPackets)
                    {
                        MainPlayQueue.Dequeue();
                    }
                }

                // ожидаем разрешения на воспроизведение
                PlayResetEvent.WaitOne();

                // если в очереди что-то есть
                while (MainPlayQueue.Count > 0)
                {
                    // достаём один буфер
                    var playingBuffer = MainPlayQueue.Dequeue();
                    // дописываем сэмплы в конец
                    BufferedWaveProvider.AddSamples(playingBuffer, 0, playingBuffer.Length);
                    if (WaveOut.PlaybackState != PlaybackState.Playing)
                    {
                        // если вдруг между последним приёмом и текущим воспроизведением плеер успел остановиться - запускаем его
                        WaveOut.Play();
                    }
                }

                // ожидаем 1 миллисекунду, чтобы поток бился в припадке скорости
                Thread.Sleep(1);
            }
        }

        // обработка получения данных от сервера
        private static void OnSocketEndReceive(IAsyncResult ar)
        {
            // пробуем получить данные
            try
            {
                // количество полученных байт 
                var receivedBytesCount = MainSocket.EndReceive(ar);
                // Console.WriteLine("Recv data");

                // создаём буфер
                byte[] playBytes = new byte[receivedBytesCount];
                // копируем данные в буфер
                Buffer.BlockCopy(MainSocketBuffer, 0, playBytes, 0, receivedBytesCount);

                // добавляем буфер в очередь
                MainPlayQueue.Enqueue(playBytes);

                // разрешаем воспроизведение
                PlayResetEvent.Set();

                // заново запускаем получение данных от сервера
                MainSocket.BeginReceive(MainSocketBuffer, 0, SendingBufferSize, SocketFlags.None,
                    OnSocketEndReceive, null);
            }
            catch (SocketException)
            {
                // если не удалось - сокет отключен (маловероятно в реальных условиях)
                Console.WriteLine("Host lost");
                IsSocketConnected = false;
                MainSocket.Close();
            }
        }

        // обработка записи с микрофона
        private static void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            // создаём буфер
            byte[] recordBytes = new byte[e.BytesRecorded];

            // копируем в него записанные данные 
            Buffer.BlockCopy(e.Buffer, 0, recordBytes, 0, e.BytesRecorded);

            // добавляем буфер в очередь отправки
            SendingQueue.Enqueue(recordBytes);
        }
    }
}