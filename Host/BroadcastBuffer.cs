using System.Collections.Generic;

namespace Host
{
    // буфер рассылки
    public class BroadcastBuffer
    {
        // текущий буфер байт для последовательного доступа
        public byte[] CurrentBuffer { get; private set; }

        // позиция в текущем буфере
        public int Position { get; private set; }

        // очередь буферов рассылки
        public Queue<byte[]> Queue { get; private set; }

        // получение следующего байт к рассылке (связывает последовательно очередь буферов)
        public byte GetByte()
        {
            // если текущий буфер пуст или мы достигли конца
            if (CurrentBuffer == null || Position == CurrentBuffer.Length)
            {
                // если есть следующий буфер
                if (Queue.Count > 0)
                {
                    // подставляем следующий буфер
                    CurrentBuffer = Queue.Dequeue();
                    // читаем сначала
                    Position = 0;
                    // вызываем рекурсивное получение следующего байта
                    return GetByte();
                }
                else
                {
                    // новых буфферов нет, рассылать нечего, возвращаем 0 (тишина)
                    CurrentBuffer = null;
                    return 0;
                }
            }
            else
            {
                // текущий буфер ещё актуален

                // получаем следующий байт
                byte data = CurrentBuffer[Position];
                // сдвигаемся на следующий байт
                Position++;
                return data;
            }
        }

        public BroadcastBuffer()
        {
            Queue = new Queue<byte[]>();
        }
    }
}