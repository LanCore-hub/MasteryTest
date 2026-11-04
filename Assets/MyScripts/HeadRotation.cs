using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class HeadRotation : MonoBehaviour
{
    /*
    [SerializeField] private int valHeadX;
    [SerializeField] private int valHeadY;

    public GameObject VRCamera;

    void Update()
    {
        if (VRCamera != null)
        {
            float rawX = VRCamera.transform.eulerAngles.x;
            float rawY = VRCamera.transform.eulerAngles.y;

            // --- Обработка оси X (вверх-вниз) ---
            valHeadX = (int)rawX;

            if (valHeadX >= 0 && valHeadX <= 180)
                valHeadX = 90 - valHeadX;
            else if (valHeadX > 180)
                valHeadX = 360 - valHeadX + 90;

            valHeadX = Mathf.Clamp(valHeadX, 0, 180);

            // --- Обработка оси Y (влево-вправо) ---
            valHeadY = (int)rawY;

            if (valHeadY > 180)
                valHeadY = valHeadY - 360;

            valHeadY = (int)((valHeadY + 180f) / 360f * 180f);
            valHeadY = Mathf.Clamp(valHeadY, 0, 180);
        }
    }
    */

    [Header("VR-данные от шлема")]
    public int valHeadX;   // горизонталь (влево-вправо) - будет обновляться из VR
    public int valHeadY;   // вертикаль (вверх-вниз) - будет обновляться из VR

    [Header("Настройки UDP")]
    [SerializeField] private MyInfo MyInfoScript;
    private string espIP; // IP-адрес вашего ESP8266
    private int port = 8888;

    private UdpClient udpClient;
    private int lastSentX = -1;
    private int lastSentY = -1;

    // Переменные для VR (если у вас есть доступ к данным шлема)
    // Замените на ваши реальные переменные из VR SDK
    private float headYaw;    // поворот головы влево-вправо (горизонталь)
    private float headPitch;  // наклон головы вверх-вниз (вертикаль)

    void Start()
    {
        espIP = MyInfoScript.esp8266IpAddress;
        // Проверка IP-адреса
        if (string.IsNullOrEmpty(espIP))
        {
            Debug.LogError("Укажите правильный IP-адрес ESP8266 в поле espIP!");
            return;
        }

        try
        {
            udpClient = new UdpClient();
            Debug.Log($"UDP-клиент создан. IP: {espIP}, порт: {port}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Ошибка создания UDP-клиента: {e.Message}");
        }
    }

    void Update()
    {
        // ===== ПОЛУЧЕНИЕ ДАННЫХ ОТ VR-ШЛЕМА =====
        // Здесь вы должны получить данные от вашего VR-шлема
        // Пример для Oculus / OpenXR / SteamVR:

        // Вариант 1: Если у вас есть доступ к Transform камеры
        //headYaw = Camera.main.transform.eulerAngles.y;   // горизонталь
        //headPitch = Camera.main.transform.eulerAngles.x; // вертикаль

        // Вариант 2: Если вы используете SteamVR
        // headYaw = SteamVR_Input.GetAction("HeadYaw").GetAxis();
        // headPitch = SteamVR_Input.GetAction("HeadPitch").GetAxis();

        // Вариант 3: Если вы используете Oculus Integration
        // headYaw = OVRManager.display.GetHeadPose().orientation.eulerAngles.y;
        // headPitch = OVRManager.display.GetHeadPose().orientation.eulerAngles.x;

        // Вариант 4: Если у вас есть свой скрипт, который передаёт данные
        // headYaw = YourVRController.headYaw;
        // headPitch = YourVRController.headPitch;

        // ===== ПРЕОБРАЗОВАНИЕ УГЛОВ В ДИАПАЗОН 0..180 =====
        // Если VR выдаёт углы в диапазоне -90..90, преобразуем в 0..180
        // Если выдаёт в диапазоне -180..180, тоже преобразуем

        // Пример для диапазона -90..90 (наклон головы)
        //valHeadY = Mathf.RoundToInt(Mathf.Clamp(headPitch + 90f, 0f, 180f));

        // Пример для диапазона -180..180 (поворот головы)
        //valHeadX = Mathf.RoundToInt(Mathf.Clamp(headYaw + 180f, 0f, 360f) / 2f);

        // Упрощённый вариант (если ваши переменные уже в диапазоне 0..180)
        // valHeadX = Mathf.RoundToInt(Mathf.Clamp(headYaw, 0f, 180f));
        // valHeadY = Mathf.RoundToInt(Mathf.Clamp(headPitch, 0f, 180f));

        float rawX = Camera.main.transform.eulerAngles.x;
        float rawY = Camera.main.transform.eulerAngles.y;

        // --- Обработка оси X (вверх-вниз) ---
        valHeadX = (int)rawX;

        if (valHeadX >= 0 && valHeadX <= 180)
            valHeadX = 90 - valHeadX;
        else if (valHeadX > 180)
            valHeadX = 360 - valHeadX + 90;

        valHeadX = 180 - Mathf.Clamp(valHeadX, 0, 180);

        // --- Обработка оси Y (влево-вправо) ---
        valHeadY = (int)rawY;

        if (valHeadY > 180)
            valHeadY = valHeadY - 360;

        valHeadY = (int)((valHeadY + 180f) / 360f * 180f);
        valHeadY = 180 - Mathf.Clamp(valHeadY, 0, 180);

        // ===== ОТПРАВКА ДАННЫХ НА ESP =====
        SendAngles();
    }

    void SendAngles()
    {
        if (udpClient == null)
        {
            Debug.Log("udp не найден");
            return;
        }

        // Отправляем только если значения изменились
        if (valHeadX == lastSentX && valHeadY == lastSentY) return;

        lastSentX = valHeadX;
        lastSentY = valHeadY;

        // Формат: вертикаль,горизонталь (как ожидает Arduino Uno)
        string msg = $"{valHeadY},{valHeadX}";
        byte[] data = Encoding.ASCII.GetBytes(msg);

        try
        {
            udpClient.Send(data, data.Length, espIP, port);
            Debug.Log($"Отправлено: {msg} (Y:{valHeadY}, X:{valHeadX})");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Ошибка отправки: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (udpClient != null)
        {
            udpClient.Close();
            Debug.Log("UDP-клиент закрыт.");
        }
    }

    // ===== ДЛЯ ОТЛАДКИ (если нет VR-шлема) =====
    // Можно оставить для ручного управления клавишами W/S/A/D
    // Раскомментируйте, если нужно тестировать без шлема
    /*
    void Update()
    {
        // Ручное управление (отладка)
        if (Input.GetKeyDown(KeyCode.W))
        {
            valHeadY = Mathf.Clamp(valHeadY + 1, 0, 180);
            SendAngles();
        }
        if (Input.GetKeyDown(KeyCode.S))
        {
            valHeadY = Mathf.Clamp(valHeadY - 1, 0, 180);
            SendAngles();
        }
        if (Input.GetKeyDown(KeyCode.A))
        {
            valHeadX = Mathf.Clamp(valHeadX - 1, 0, 180);
            SendAngles();
        }
        if (Input.GetKeyDown(KeyCode.D))
        {
            valHeadX = Mathf.Clamp(valHeadX + 1, 0, 180);
            SendAngles();
        }
    }
    */
}
