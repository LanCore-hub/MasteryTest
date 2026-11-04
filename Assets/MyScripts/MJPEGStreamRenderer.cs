/*using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class MJPEGStreamRenderer : MonoBehaviour
{
    [Header("Network Settings")]
    [SerializeField] private string host = "10.103.137.139";
    [SerializeField] private int port = 81;
    [SerializeField] private string path = "/stream";

    [Header("Performance")]
    [SerializeField] private int targetFPS = 30;         // Максимальная частота обновления текстуры
    [SerializeField] private int bufferSize = 65536;     // Размер буфера (64 КБ, можно увеличить)

    private Renderer objectRenderer;
    public Texture2D currentTexture;
    private Thread streamThread;
    private volatile bool isRunning = false;
    private byte[] latestJpeg = null;
    private readonly object lockObject = new object();

    // Статистика (опционально)
    private float updateInterval = 1f;
    private float lastIntervalTime;
    private int frameCount;

    void Start()
    {
        objectRenderer = GetComponent<Renderer>();
        if (objectRenderer == null)
        {
            Debug.LogError("MJPEGStreamRenderer: Нет компонента Renderer на объекте!");
            enabled = false;
            return;
        }

        // Создаём временную текстуру, чтобы избежать null в Update
        currentTexture = new Texture2D(2, 2);
        objectRenderer.material.mainTexture = currentTexture;

        isRunning = true;
        streamThread = new Thread(StreamWorker);
        streamThread.IsBackground = true;
        streamThread.Start();

        lastIntervalTime = Time.time;
    }

    private void StreamWorker()
    {
        // Цикл переподключения при обрыве связи
        while (isRunning)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    // Подключаемся с таймаутом
                    client.Connect(host, port);
                    Debug.Log($"MJPEGStreamRenderer: Подключено к {host}:{port}");

                    using (NetworkStream networkStream = client.GetStream())
                    {
                        // Отправляем HTTP-запрос
                        string request = $"GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: keep-alive\r\n\r\n";
                        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                        networkStream.Write(requestBytes, 0, requestBytes.Length);

                        // Пропускаем HTTP-заголовки ответа до пустой строки
                        if (!SkipHttpHeaders(networkStream))
                        {
                            Debug.LogWarning("MJPEGStreamRenderer: Не удалось пропустить заголовки. Переподключение...");
                            Thread.Sleep(1000);
                            continue;
                        }

                        // Буфер для приёма данных
                        byte[] buffer = new byte[bufferSize];
                        int bufferPos = 0;
                        bool firstFrameProcessed = false;

                        while (isRunning && client.Connected)
                        {
                            // Ждём появления данных
                            if (!networkStream.DataAvailable)
                            {
                                Thread.Sleep(1);
                                continue;
                            }

                            int bytesRead = networkStream.Read(buffer, bufferPos, buffer.Length - bufferPos);
                            if (bytesRead == 0) break; // Соединение закрыто

                            bufferPos += bytesRead;

                            // Обрабатываем все полные JPEG-кадры в буфере
                            int processedBytes = 0;
                            while (true)
                            {
                                // Ищем начало JPEG (FF D8)
                                int start = FindPattern(buffer, processedBytes, bufferPos - processedBytes, new byte[] { 0xFF, 0xD8 });
                                if (start == -1) break;

                                // Ищем конец JPEG (FF D9)
                                int end = FindPattern(buffer, start + 2, bufferPos - start - 2, new byte[] { 0xFF, 0xD9 });
                                if (end == -1) break;
                                end += 2; // включаем два байта маркера конца

                                int jpegLen = end - start;
                                byte[] jpeg = new byte[jpegLen];
                                Array.Copy(buffer, start, jpeg, 0, jpegLen);

                                // Сохраняем кадр для основного потока
                                lock (lockObject)
                                {
                                    latestJpeg = jpeg;
                                }

                                // Сдвигаем буфер: удаляем обработанный кадр
                                int remaining = bufferPos - end;
                                if (remaining > 0)
                                    Array.Copy(buffer, end, buffer, 0, remaining);
                                bufferPos = remaining;
                                processedBytes = 0;

                                firstFrameProcessed = true;

                                // Ограничение FPS – небольшая задержка после каждого кадра
                                if (targetFPS > 0)
                                    Thread.Sleep(1000 / targetFPS);
                            }

                            // Если буфер переполнен, но целого кадра нет – сбрасываем его во избежание утечки памяти
                            if (bufferPos >= buffer.Length)
                            {
                                Debug.LogWarning("MJPEGStreamRenderer: Буфер переполнен, сброс.");
                                bufferPos = 0;
                            }
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                Debug.LogError($"MJPEGStreamRenderer: Ошибка сокета: {ex.Message}. Переподключение через 2 сек...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"MJPEGStreamRenderer: Общая ошибка: {ex.Message}. Переподключение через 2 сек...");
            }

            if (isRunning)
                Thread.Sleep(2000); // Пауза перед переподключением
        }
    }

    /// <summary>
    /// Пропускает HTTP-заголовки ответа до пустой строки.
    /// </summary>
    private bool SkipHttpHeaders(NetworkStream stream)
    {
        byte[] buffer = new byte[4096];
        int totalRead = 0;
        int headerEndPos = -1;

        while (headerEndPos == -1)
        {
            if (!stream.DataAvailable && totalRead == 0)
                Thread.Sleep(10);

            int bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (bytesRead == 0) return false;

            totalRead += bytesRead;
            headerEndPos = FindHeaderEnd(buffer, totalRead);
        }

        // Убираем заголовки из потока: остаются только данные после \r\n\r\n
        int dataStart = headerEndPos + 4; // после "\r\n\r\n"
        if (dataStart < totalRead)
        {
            // Оставшиеся данные (начало первого JPEG) нужно вернуть в поток? 
            // В нашей архитектуре проще их проигнорировать, первый кадр будет прочитан заново.
            // Но можно и сохранить в отдельный буфер. Для простоты пропускаем.
        }
        return true;
    }

    private int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i < length - 3; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Безопасный поиск паттерна в массиве байт.
    /// </summary>
    private int FindPattern(byte[] data, int start, int length, byte[] pattern)
    {
        if (data == null || pattern == null || start < 0 || length < 0)
            return -1;

        int end = Math.Min(data.Length, start + length) - pattern.Length;
        if (end < start) return -1;

        for (int i = start; i <= end; i++)
        {
            bool found = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    found = false;
                    break;
                }
            }
            if (found) return i;
        }
        return -1;
    }

    void Update()
    {
        // Обновляем текстуру, если есть новый кадр
        if (latestJpeg != null)
        {
            lock (lockObject)
            {
                if (currentTexture != null && currentTexture.LoadImage(latestJpeg))
                {
                    currentTexture.wrapMode = TextureWrapMode.Clamp;
                    // Применяем текстуру к материалу
                    if (objectRenderer != null)
                        objectRenderer.material.mainTexture = currentTexture;

                    frameCount++;
                }
                latestJpeg = null;
            }
        }

        // Вывод статистики FPS в консоль (можно закомментировать)
        if (Time.time - lastIntervalTime >= updateInterval)
        {
            float fps = frameCount / updateInterval;
            //Debug.Log($"MJPEG FPS: {fps:F1}");
            frameCount = 0;
            lastIntervalTime = Time.time;
        }
    }

    void OnDestroy()
    {
        isRunning = false;
        if (streamThread != null && streamThread.IsAlive)
        {
            streamThread.Join(1000);
        }
        if (currentTexture != null)
            Destroy(currentTexture);
    }

    void OnApplicationQuit()
    {
        isRunning = false;
    }
}*/

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class MJPEGStreamRenderer : MonoBehaviour
{
    [Header("Network Settings")]
    [SerializeField] private string host;
    [SerializeField] private int port = 81;
    [SerializeField] private string path = "/stream";
    [SerializeField] private int connectTimeoutMs = 3000;
    [SerializeField] private int readTimeoutMs = 5000;   // если камера молчит дольше — переподключение

    [Header("Performance")]
    [SerializeField] private int targetFPS = 30;         // Максимальная частота обновления текстуры (0 = без ограничения)

    [Header("Photos")]
    [SerializeField] private string photoFolderName = "Картинки"; // Папка на рабочем столе

    private const int InitialBufferSize = 256 * 1024;
    private const int MaxBufferSize = 4 * 1024 * 1024;  // защита от бесконечного роста при мусоре в потоке
    private const int MinFreeSpace = 16 * 1024;

    public Texture2D currentTexture;

    private Renderer objectRenderer;
    private Material material;
    private Thread streamThread;
    private volatile bool isRunning;
    private volatile TcpClient activeClient;

    private byte[] pendingJpeg;    // свежий кадр из сетевого потока (обмен через Interlocked)
    private byte[] lastShownJpeg;  // последний показанный кадр — его и сохраняем как фото
    private float nextDecodeTime;

    void Start()
    {
        objectRenderer = GetComponent<Renderer>();
        if (objectRenderer == null)
        {
            Debug.LogError("MJPEGStreamRenderer: Нет компонента Renderer на объекте!");
            enabled = false;
            return;
        }

        // RGB24 без мипмапов: JPEG всё равно без альфы, а генерация мипмапов на каждом кадре — лишняя работа
        currentTexture = new Texture2D(2, 2, TextureFormat.RGB24, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        // .material создаёт копию материала — берём её один раз и дальше только обновляем текстуру
        material = objectRenderer.material;
        material.mainTexture = currentTexture;

        isRunning = true;
        streamThread = new Thread(StreamWorker) { IsBackground = true, Name = "MJPEG Stream" };
        streamThread.Start();
    }

    private void StreamWorker()
    {
        // Цикл переподключения при обрыве связи
        while (isRunning)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    activeClient = client;
                    client.NoDelay = true;
                    client.ReceiveBufferSize = InitialBufferSize;

                    // Подключаемся с таймаутом (обычный Connect может висеть ~20 сек)
                    Task connect = client.ConnectAsync(host, port);
                    if (!connect.Wait(connectTimeoutMs) || !client.Connected)
                        throw new TimeoutException($"нет ответа от {host}:{port}");

                    using (NetworkStream networkStream = client.GetStream())
                    {
                        networkStream.ReadTimeout = readTimeoutMs;

                        string request = $"GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: keep-alive\r\n\r\n";
                        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                        networkStream.Write(requestBytes, 0, requestBytes.Length);
                        Debug.Log($"MJPEGStreamRenderer: Подключено к {host}:{port}");

                        ReadFrames(networkStream);
                    }
                }
            }
            catch (Exception ex)
            {
                if (isRunning)
                    Debug.LogWarning($"MJPEGStreamRenderer: {ex.GetBaseException().Message}. Переподключение через 2 сек...");
            }
            finally
            {
                activeClient = null;
            }

            if (isRunning)
                Thread.Sleep(2000);
        }
    }

    /// <summary>
    /// Читает поток и вырезает из него JPEG-кадры по маркерам FF D8 ... FF D9.
    /// HTTP- и multipart-заголовки — это ASCII-текст без байта 0xFF, поэтому их отдельно пропускать не нужно.
    /// </summary>
    private void ReadFrames(NetworkStream stream)
    {
        byte[] buf = new byte[InitialBufferSize];
        int len = 0;          // сколько байт в буфере
        int scan = 0;         // откуда продолжать поиск, чтобы не просматривать данные повторно
        int frameStart = -1;  // начало текущего кадра, если FF D8 уже найден

        while (isRunning)
        {
            // Буфер растёт под большой кадр (высокое разрешение камеры легко даёт > 64 КБ)
            if (buf.Length - len < MinFreeSpace)
            {
                if (buf.Length >= MaxBufferSize)
                {
                    Debug.LogWarning("MJPEGStreamRenderer: Буфер переполнен, сброс.");
                    len = 0; scan = 0; frameStart = -1;
                }
                else
                {
                    Array.Resize(ref buf, buf.Length * 2);
                }
            }

            // Блокирующее чтение вместо опроса DataAvailable + Sleep(1): меньше задержка и нагрузка на CPU
            int bytesRead = stream.Read(buf, len, buf.Length - len);
            if (bytesRead <= 0) return; // соединение закрыто
            len += bytesRead;

            while (true)
            {
                if (frameStart < 0)
                {
                    frameStart = FindMarker(buf, scan, len, 0xD8);
                    if (frameStart < 0) { scan = Math.Max(0, len - 1); break; }
                    scan = frameStart + 2;
                }

                int end = FindMarker(buf, scan, len, 0xD9);
                if (end < 0) { scan = Math.Max(frameStart + 2, len - 1); break; }
                end += 2;

                byte[] jpeg = new byte[end - frameStart];
                Buffer.BlockCopy(buf, frameStart, jpeg, 0, jpeg.Length);

                // Кладём только самый свежий кадр; если Update не успел забрать предыдущий — он просто заменяется.
                Interlocked.Exchange(ref pendingJpeg, jpeg);

                frameStart = -1;
                scan = end;
            }

            // Сдвигаем буфер: выбрасываем всё, что уже обработано
            int keepFrom = frameStart >= 0 ? frameStart : scan;
            if (keepFrom > 0)
            {
                Buffer.BlockCopy(buf, keepFrom, buf, 0, len - keepFrom);
                len -= keepFrom;
                scan -= keepFrom;
                if (frameStart >= 0) frameStart = 0;
            }
        }
    }

    /// <summary>
    /// Ищет пару байт 0xFF, second в диапазоне [from, to).
    /// </summary>
    private static int FindMarker(byte[] data, int from, int to, byte second)
    {
        for (int i = from; i < to - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == second)
                return i;
        }
        return -1;
    }

    void Update()
    {
        if (targetFPS > 0 && Time.unscaledTime < nextDecodeTime)
            return;

        byte[] jpeg = Interlocked.Exchange(ref pendingJpeg, null);
        if (jpeg == null)
            return;

        // Текстура та же самая, LoadImage меняет её содержимое — переназначать в материал не нужно
        if (currentTexture.LoadImage(jpeg, false))
        {
            lastShownJpeg = jpeg;
            if (targetFPS > 0)
                nextDecodeTime = Time.unscaledTime + 1f / targetFPS;
        }
    }

    /// <summary>
    /// Сохраняет текущий кадр в папку photoFolderName на рабочем столе.
    /// Сохраняются исходные байты JPEG от камеры — без перекодирования и потери качества.
    /// </summary>
    public void TakePhoto()
    {
        byte[] jpeg = lastShownJpeg;
        if (jpeg == null)
        {
            Debug.LogWarning("MJPEGStreamRenderer: Нет кадра для сохранения (камера ещё не подключена?)");
            return;
        }

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), photoFolderName);
        string file = Path.Combine(folder, $"Photo_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.jpg");

        // Запись на диск в фоне, чтобы в VR не было подёргивания кадра
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(file, jpeg);
                Debug.Log($"MJPEGStreamRenderer: Фото сохранено: {file}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"MJPEGStreamRenderer: Не удалось сохранить фото: {ex.Message}");
            }
        });
    }

    void OnDestroy()
    {
        isRunning = false;
        activeClient?.Close(); // прерывает зависший Read, чтобы поток завершился сразу
        if (streamThread != null && streamThread.IsAlive)
            streamThread.Join(1000);

        if (currentTexture != null)
            Destroy(currentTexture);
        if (material != null)
            Destroy(material);
    }

    void OnApplicationQuit()
    {
        isRunning = false;
    }
}
