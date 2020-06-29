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
        // частота дискретизации записи
        public static int RecordingSampleRate = 44100;

        // количество каналов записи
        public static int RecordingChannels = 1;

        // 16 бит формат по умолчанию
        public static int RecordingBytesPerSample = RecordingChannels * 2;

        // частота рассылки сервера
        public static int SendingFrequency = 16;

        // размер отправляемого буфера
        public static int SendingBufferSize = RecordingSampleRate * RecordingBytesPerSample / SendingFrequency;

        // максимальное количество пакетов в очереди на отправку
        public static int MaxBufferedPackets = 5;

        // Главный сокет
        private static readonly Socket ListenSocket =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // список подключенных клиентов
        private static readonly List<ClientData> ConnectedClients = new List<ClientData>();

        public static void Main(string[] args)
        {
            // устанавливаем прослушку на сокете
            ListenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 11771));
            ListenSocket.Listen(10);
            ListenSocket.BeginAccept(OnAcceptClient, null);

            Console.WriteLine("Server started");

            // запускаем поток рассылки
            Thread broadcastThread = new Thread(BroadcastThreadJob);
            broadcastThread.Start();

            Console.ReadKey();

            broadcastThread.Abort();
        }

        private static void BroadcastThreadJob()
        {
            const double pow2_15 = 1 << 15; // константа 2^15 вынесена для ускорения вычислений

            while (Thread.CurrentThread.ThreadState != ThreadState.AbortRequested)
            {
                // если клиентов нет - просто ждём
                if (ConnectedClients.Count == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                // TODO: Исправить чтобы не отправляло обратно сэмплы клиенту, которые их отправил

                byte[] finalSendingBuffer = new byte[SendingBufferSize];
                for (int b = 0; b < SendingBufferSize; b += 2)
                {
                    // смешивание сэмплов
                    float[] samplesFromClients = new float[ConnectedClients.Count];
                    for (int i = 0; i < ConnectedClients.Count; i++)
                    {
                        // освобождаем очередь пакетов
                        while (ConnectedClients[i].BroadcastBuffer.Queue.Count >= MaxBufferedPackets)
                        {
                            ConnectedClients[i].BroadcastBuffer.Queue.Dequeue();
                        }

                        // вытаскиваем 2 байта из очереди от клиента
                        var firstByte = ConnectedClients[i].BroadcastBuffer.GetByte();
                        var secondByte = ConnectedClients[i].BroadcastBuffer.GetByte();

                        // распаковываем PCM сэмпл
                        short sample = (short) (firstByte | (secondByte << 8));

                        //записываем сэмпл от клиента
                        samplesFromClients[i] = (float) (sample / pow2_15);
                    }

                    // ищем максимальное значение сэмпла
                    float maxSample = samplesFromClients.Max();

                    // конвертируем в short 
                    // PCM формат https://audiocoding.ru/articles/2008-05-22-wav-file-structure/wav_formats.txt
                    short maxSampleShort = (short) (maxSample * pow2_15);

                    // кодируем сэмпл в байты
                    byte sendingFirstByte = (byte) (maxSampleShort & 0xff);
                    byte sendingSecondByte = (byte) ((maxSampleShort >> 8) & 0xff);

                    // записываем байты в очередь на отправку
                    finalSendingBuffer[b] = sendingFirstByte;
                    finalSendingBuffer[b + 1] = sendingSecondByte;
                }

                // всем подключенным клиентам
                for (int i = 0; i < ConnectedClients.Count; i++)
                {
                    try
                    {
                        // пробуем отправить данные
                        ConnectedClients[i].Socket.Send(finalSendingBuffer);
                    }
                    catch (SocketException)
                    {
                        // если словили ошибку - клиент отключился
                        Console.WriteLine("Client lost");
                        ConnectedClients[i].Socket.Close();
                        ConnectedClients.RemoveAt(i);
                        i--;
                    }
                }

                // Console.WriteLine($"Sent {finalSendingBuffer.Length}");

                // здесь обязательно ожидаем нужное количество времени, чтобы не забить буферы пустотой клиентам
                Thread.Sleep(1000 / SendingFrequency);
            }
        }

        // обработка подключения клиента
        private static void OnAcceptClient(IAsyncResult ar)
        {
            try
            {
                // пробуем подключить клиента
                Socket clientSocket = ListenSocket.EndAccept(ar);
                Console.WriteLine("Client connected");

                // собираем клиенту объект состояни
                ClientData clientData = new ClientData(clientSocket, SendingBufferSize);

                // добавляем клиента в список
                ConnectedClients.Add(clientData);

                // запускаем получение от клиента
                clientSocket.BeginReceive(clientData.SocketReceiveBuffer, 0, clientData.SocketReceiveBuffer.Length,
                    SocketFlags.None,
                    OnReceiveFromClient, clientData);
            }
            catch (SocketException)
            {
                // не удалось подключить клиента (маловероятно в реальном сценарии)
                Console.WriteLine("Exception in Accept");
            }

            // запускаем ожидание подключения следующего клиента
            ListenSocket.BeginAccept(OnAcceptClient, null);
        }

        // обработка получения данных от клиента
        private static void OnReceiveFromClient(IAsyncResult ar)
        {
            // получаем переданный объект состояния клиента
            var clientData = (ClientData) ar.AsyncState;
            try
            {
                // реальное количество переданных байт
                var receivedFromClient = clientData.Socket.EndReceive(ar);

                // Console.WriteLine($"\tRecv {receivedFromClient}");

                // создаём буфер
                byte[] bufferForUserData = new byte[receivedFromClient];

                // копируем буфер
                Buffer.BlockCopy(clientData.SocketReceiveBuffer, 0, bufferForUserData, 0, receivedFromClient);

                // добавляем буфер в очередь рассылки
                clientData.BroadcastBuffer.Queue.Enqueue(bufferForUserData);

                // запускаем получение от клиента снова
                clientData.Socket.BeginReceive(clientData.SocketReceiveBuffer, 0,
                    clientData.SocketReceiveBuffer.Length, SocketFlags.None, OnReceiveFromClient, clientData);
            }
            catch (SocketException)
            {
                // ошибка при получении - значит клиент отключился
                Console.WriteLine("Client disconnected");
                clientData.Socket.Close();
                ConnectedClients.Remove(clientData);
            }
        }
    }
}