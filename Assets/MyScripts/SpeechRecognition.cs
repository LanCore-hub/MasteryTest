using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Windows.Speech;

public class SpeechRecognition : MonoBehaviour
{
    private DictationRecognizer dictationRecognizer;
    private bool isRestarting = false;
    private string esp8266IpAddress = "192.168.88.28"; // Замените на IP вашего ESP8266

    [Header("Settings")]
    public float confidenceThreshold = 0.5f;

    // Для управления клавишами
    private bool upPressed = false;
    private bool downPressed = false;
    private bool leftPressed = false;
    private bool rightPressed = false;

    void Start()
    {
        InitializeDictation();
    }

    void Update()
    {
        // Управление с клавиатуры для тестирования
        HandleKeyboardInput();
    }

    private void HandleKeyboardInput()
    {
        // Вперед (W или стрелка вверх)
        if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow))
        {
            SendCommandToESP8266("/forward");
        }

        // Назад (S или стрелка вниз)
        if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow))
        {
            SendCommandToESP8266("/backward");
        }

        // Влево (A или стрелка влево)
        if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow))
        {
            SendCommandToESP8266("/left");
        }

        // Вправо (D или стрелка вправо)
        if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow))
        {
            SendCommandToESP8266("/right");
        }

        // Стоп (Space или Escape)
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape))
        {
            SendCommandToESP8266("/stop");
        }

        // Отпускание клавиш
        if (Input.GetKeyUp(KeyCode.W) || Input.GetKeyUp(KeyCode.UpArrow) ||
            Input.GetKeyUp(KeyCode.S) || Input.GetKeyUp(KeyCode.DownArrow) ||
            Input.GetKeyUp(KeyCode.A) || Input.GetKeyUp(KeyCode.LeftArrow) ||
            Input.GetKeyUp(KeyCode.D) || Input.GetKeyUp(KeyCode.RightArrow))
        {
            SendCommandToESP8266("/stop");
        }
    }

    private void InitializeDictation()
    {
        if (!PhraseRecognitionSystem.isSupported)
        {
            Debug.LogError("Speech recognition is not supported on this device.");
            return;
        }

        if (dictationRecognizer != null)
        {
            dictationRecognizer.Dispose();
        }

        dictationRecognizer = new DictationRecognizer();

        dictationRecognizer.DictationResult += OnDictationResult;
        dictationRecognizer.DictationHypothesis += OnDictationHypothesis;
        dictationRecognizer.DictationComplete += OnDictationComplete;
        dictationRecognizer.DictationError += OnDictationError;

        dictationRecognizer.Start();
        Debug.Log("Continuous speech recognition started! Start speaking...");
    }

    private void OnDictationResult(string text, ConfidenceLevel confidence)
    {
        Debug.Log($"Recognized: '{text}' (Confidence: {confidence})");
        ProcessVoiceCommand(text);
    }

    private void OnDictationHypothesis(string text)
    {
        Debug.Log($"Hypothesis: {text}");
        ProcessVoiceCommand(text, true);
    }

    private void OnDictationComplete(DictationCompletionCause cause)
    {
        Debug.Log($"Dictation completed: {cause}");

        if (!isRestarting)
        {
            StartCoroutine(RestartDictation());
        }
    }

    private void OnDictationError(string error, int hresult)
    {
        Debug.LogError($"Dictation error: {error} (HRESULT: {hresult})");

        if (!isRestarting)
        {
            StartCoroutine(RestartDictation());
        }
    }

    private IEnumerator RestartDictation()
    {
        if (isRestarting) yield break;

        isRestarting = true;
        yield return new WaitForSeconds(1f);

        if (dictationRecognizer != null)
        {
            dictationRecognizer.Stop();
            dictationRecognizer.Dispose();
            dictationRecognizer = null;
        }

        yield return new WaitForSeconds(0.5f);
        InitializeDictation();
        isRestarting = false;
        Debug.Log("Dictation restarted successfully");
    }

    private void ProcessVoiceCommand(string command, bool isHypothesis = false)
    {
        command = command.ToLower().Trim();

        // Если это гипотеза и уверенность низкая, можно пропустить обработку
        if (isHypothesis && dictationRecognizer != null)
        {
            // Можно добавить проверку уверенности
        }

        // Обработка команд движения на русском и английском
        if (command.Contains("вперед") || command.Contains("forward") ||
            command.Contains("вверх") || command.Contains("up"))
        {
            Debug.Log("Executing: Move Forward");
            SendCommandToESP8266("/forward");
        }
        else if (command.Contains("назад") || command.Contains("backward") ||
                 command.Contains("back") || command.Contains("вниз") ||
                 command.Contains("down"))
        {
            Debug.Log("Executing: Move Backward");
            SendCommandToESP8266("/backward");
        }
        else if (command.Contains("влево") || command.Contains("left"))
        {
            Debug.Log("Executing: Turn Left");
            SendCommandToESP8266("/left");
        }
        else if (command.Contains("вправо") || command.Contains("right"))
        {
            Debug.Log("Executing: Turn Right");
            SendCommandToESP8266("/right");
        }
        else if (command.Contains("стоп") || command.Contains("stop") ||
                 command.Contains("остановись") || command.Contains("стой"))
        {
            Debug.Log("Executing: Stop");
            SendCommandToESP8266("/stop");
        }
        // Команды для движения вперед/назад с уточнениями
        else if (command.Contains("прямо") || command.Contains("ехай прямо"))
        {
            Debug.Log("Executing: Go Straight");
            SendCommandToESP8266("/forward");
        }
        else if (command.Contains("разворот") || command.Contains("развернись"))
        {
            Debug.Log("Executing: Turn Around");
            // Последовательность команд для разворота
            StartCoroutine(TurnAround());
        }
    }

    private IEnumerator TurnAround()
    {
        SendCommandToESP8266("/left");
        yield return new WaitForSeconds(0.5f);
        SendCommandToESP8266("/left");
        yield return new WaitForSeconds(0.5f);
        SendCommandToESP8266("/stop");
    }

    // Метод для отправки команд на ESP8266
    public void SendCommandToESP8266(string command)
    {
        StartCoroutine(SendCommandCoroutine(command));
    }

    private IEnumerator SendCommandCoroutine(string command)
    {
        string url = $"http://{esp8266IpAddress}{command}";

        Debug.Log($"Sending command to: {url}");

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            www.timeout = 3; // Таймаут 3 секунды

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"Command {command} sent successfully. Response: {www.downloadHandler.text}");
            }
            else
            {
                Debug.LogError($"Failed to send command: {www.error}");
                Debug.LogError($"URL: {url}");
                Debug.LogError($"Response Code: {www.responseCode}");

                // Попытка повторной отправки
                yield return new WaitForSeconds(0.5f);
                StartCoroutine(SendCommandCoroutine(command));
            }
        }
    }

    // Методы для кнопок UI (если будете добавлять кнопки в интерфейс)
    public void MoveForward()
    {
        SendCommandToESP8266("/forward");
    }

    public void MoveBackward()
    {
        SendCommandToESP8266("/backward");
    }

    public void TurnLeft()
    {
        SendCommandToESP8266("/left");
    }

    public void TurnRight()
    {
        SendCommandToESP8266("/right");
    }

    public void Stop()
    {
        SendCommandToESP8266("/stop");
    }

    public void StartListening()
    {
        if (dictationRecognizer != null && dictationRecognizer.Status != SpeechSystemStatus.Running)
        {
            dictationRecognizer.Start();
            Debug.Log("Started listening...");
        }
    }

    public void StopListening()
    {
        if (dictationRecognizer != null && dictationRecognizer.Status == SpeechSystemStatus.Running)
        {
            dictationRecognizer.Stop();
            Debug.Log("Stopped listening");
        }
    }

    private void OnDestroy()
    {
        if (dictationRecognizer != null)
        {
            dictationRecognizer.DictationResult -= OnDictationResult;
            dictationRecognizer.DictationHypothesis -= OnDictationHypothesis;
            dictationRecognizer.DictationComplete -= OnDictationComplete;
            dictationRecognizer.DictationError -= OnDictationError;

            dictationRecognizer.Stop();
            dictationRecognizer.Dispose();
            dictationRecognizer = null;
        }
    }

    private void OnApplicationQuit()
    {
        if (dictationRecognizer != null)
        {
            dictationRecognizer.Dispose();
            dictationRecognizer = null;
        }

        // Отправляем команду стоп при выходе
        SendCommandToESP8266("/stop");
    }
}