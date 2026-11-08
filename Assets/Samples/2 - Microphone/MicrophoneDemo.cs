using System;
using System.Collections;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using Whisper.Utils;
using Button = UnityEngine.UI.Button;
using Toggle = UnityEngine.UI.Toggle;
using Debug = UnityEngine.Debug;

namespace Whisper.Samples
{
    public class MicrophoneDemo : MonoBehaviour
    {
        public WhisperManager whisper;
        public MicrophoneRecord microphoneRecord;
        public bool streamSegments = true;
        public bool printLanguage = true;

        [Header("UI")]
        public Button button;
        public Text buttonText;
        public Text outputText;
        public Text timeText;
        public Dropdown languageDropdown;
        public Toggle translateToggle;
        public Toggle vadToggle;
        public ScrollRect scroll;

        [Header("Signs")]
        [SerializeField] private GameObject Arrow_sign;
        [SerializeField] private GameObject Stop_sign;

        [Header("3D Text")]
        public TMP_Text text3D;
        public float text3DDuration = 5f;
        public bool autoHideText3D = true;
        private float _text3DTimer;

        [Header("ESP8266")]
        [SerializeField] private MyInfo MyInfoScript;
        private string esp8266IpAddress;

        [Header("Keyboard Control")]
        [Tooltip("Включить управление с клавиатуры (стрелки + пробел)")]
        public bool useKeyboardControl = true;

        [Header("SteamVR Stop Action")]
        [Tooltip("Действие StopGrab из SteamVR Input (Boolean). Обычно /actions/default/in/StopGrab")]
        public SteamVR_Action_Boolean stopGrabAction;
        [Tooltip("Источник ввода: LeftHand, RightHand или Any")]
        public SteamVR_Input_Sources inputSource = SteamVR_Input_Sources.Any;
        [Tooltip("Отправлять /stop только один раз за нажатие")]
        public bool stopOncePerPress = true;
        [Tooltip("Печатать отладку SteamVR-событий")]
        public bool debugSteamVR = true;

        private bool _gripPressed = false;
        private string _buffer;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        private void Awake()
        {
            esp8266IpAddress = MyInfoScript.esp8266IpAddress;

            whisper.OnNewSegment += OnNewSegment;
            whisper.OnProgress += OnProgressHandler;

            microphoneRecord.OnRecordStop += OnRecordStop;

            button.onClick.AddListener(OnButtonPressed);
            languageDropdown.value = languageDropdown.options
                .FindIndex(op => op.text == whisper.language);
            languageDropdown.onValueChanged.AddListener(OnLanguageChanged);

            translateToggle.isOn = whisper.translateToEnglish;
            translateToggle.onValueChanged.AddListener(OnTranslateChanged);

            vadToggle.isOn = microphoneRecord.vadStop;
            vadToggle.onValueChanged.AddListener(OnVadChanged);

            if (text3D != null)
            {
                text3D.gameObject.SetActive(false);
            }

            // ===== SteamVR StopGrab =====
            if (stopGrabAction != null)
            {
                stopGrabAction.AddOnStateDownListener(OnStopGrabDown, inputSource);
                stopGrabAction.AddOnStateUpListener(OnStopGrabUp, inputSource);
                if (debugSteamVR)
                    Debug.Log($"[SteamVR] Подписались на '{stopGrabAction.fullPath}' (source={inputSource})");
            }
            else
            {
                // Пробуем автоматически найти StopGrab в сгенерированных SteamVR_Actions
                var autoAction = SteamVR_Actions.default_StopGrab;
                if (autoAction != null)
                {
                    stopGrabAction = autoAction;
                    stopGrabAction.AddOnStateDownListener(OnStopGrabDown, inputSource);
                    stopGrabAction.AddOnStateUpListener(OnStopGrabUp, inputSource);
                    if (debugSteamVR)
                        Debug.Log($"[SteamVR] stopGrabAction не назначен, взяли default_StopGrab автоматически. Path='{stopGrabAction.fullPath}'");
                }
                else
                {
                    Debug.LogError("[SteamVR] Не удалось найти StopGrab! Назначьте его в инспекторе или сгенерируйте SteamVR Input Actions.");
                }
            }

            if (string.IsNullOrWhiteSpace(esp8266IpAddress))
            {
                Debug.LogError("[ESP] esp8266IpAddress пустой! Укажите IP в инспекторе.");
            }
            else
            {
                Debug.Log($"[ESP] IP in inspector: '{esp8266IpAddress}' len={esp8266IpAddress.Length}");
            }
        }

        private void OnDestroy()
        {
            if (stopGrabAction != null)
            {
                stopGrabAction.RemoveOnStateDownListener(OnStopGrabDown, inputSource);
                stopGrabAction.RemoveOnStateUpListener(OnStopGrabUp, inputSource);
            }
        }

        private void Start()
        {
            if (!microphoneRecord.IsRecording)
            {
                microphoneRecord.StartRecord();
                buttonText.text = "Stop";
            }
        }

        private void Update()
        {
            if (autoHideText3D && text3D != null && text3D.gameObject.activeSelf)
            {
                _text3DTimer -= Time.deltaTime;
                if (_text3DTimer <= 0)
                {
                    text3D.gameObject.SetActive(false);
                }
            }

            if (useKeyboardControl)
            {
                HandleKeyboardInput();
            }

            // ===== Резервный опрос StopGrab =====
            if (stopGrabAction != null)
            {
                bool down = stopGrabAction.GetStateDown(inputSource);
                bool up = stopGrabAction.GetStateUp(inputSource);
                if (down) HandleStopGrabDown("poll");
                if (up) HandleStopGrabUp("poll");
            }
        }

        private void HandleKeyboardInput()
        {
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(0, 90, 270);
                    Arrow_sign.SetActive(true);
                }
                UpdateText3D("ВПЕРЁД");
                SendCommandToESP8266("/forward");
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(0, 90, 90);
                    Arrow_sign.SetActive(true);
                }
                UpdateText3D("НАЗАД");
                SendCommandToESP8266("/backward");
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(90, 0, 0);
                    Arrow_sign.SetActive(true);
                }
                UpdateText3D("ВЛЕВО");
                SendCommandToESP8266("/left");
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(90, 0, 180);
                    Arrow_sign.SetActive(true);
                }
                UpdateText3D("ВПРАВО");
                SendCommandToESP8266("/right");
            }
            else if (Input.GetKeyDown(KeyCode.Space))
            {
                if (Arrow_sign != null) Arrow_sign.SetActive(false);
                if (Stop_sign != null) Stop_sign.SetActive(true);
                UpdateText3D("СТОП");
                SendCommandToESP8266("/stop");
            }
        }

        private void OnStopGrabDown(SteamVR_Action_Boolean fromAction, SteamVR_Input_Sources fromSource)
        {
            HandleStopGrabDown($"event({fromSource})");
        }

        private void OnStopGrabUp(SteamVR_Action_Boolean fromAction, SteamVR_Input_Sources fromSource)
        {
            HandleStopGrabUp($"event({fromSource})");
        }

        private void HandleStopGrabDown(string source)
        {
            if (stopOncePerPress && _gripPressed) return;
            _gripPressed = true;

            if (debugSteamVR)
                Debug.Log($"[SteamVR] STOPGRAB DOWN ({source}) -> отправляем /stop");

            if (Arrow_sign != null) Arrow_sign.SetActive(false);
            if (Stop_sign != null) Stop_sign.SetActive(true);

            UpdateText3D("STOP");
            SendCommandToESP8266("/stop");
        }

        private void HandleStopGrabUp(string source)
        {
            if (!_gripPressed) return;
            _gripPressed = false;

            if (debugSteamVR)
                Debug.Log($"[SteamVR] STOPGRAB UP ({source})");
        }

        // ===== UI =====

        private void OnVadChanged(bool vadStop)
        {
            microphoneRecord.vadStop = vadStop;
        }

        private void OnButtonPressed()
        {
            if (!microphoneRecord.IsRecording)
            {
                microphoneRecord.StartRecord();
                buttonText.text = "Stop";
            }
            else
            {
                microphoneRecord.StopRecord();
                buttonText.text = "Record";
            }
        }

        private async void OnRecordStop(AudioChunk recordedAudio)
        {
            buttonText.text = "Record";
            _buffer = "";

            var sw = new Stopwatch();
            sw.Start();

            var res = await whisper.GetTextAsync(recordedAudio.Data, recordedAudio.Frequency, recordedAudio.Channels);
            if (res == null || !outputText)
                return;

            var time = sw.ElapsedMilliseconds;
            var rate = recordedAudio.Length / (time * 0.001f);
            timeText.text = $"Time: {time} ms\nRate: {rate:F1}x";

            var text = res.Result;
            if (printLanguage)
                text += $"\n\nLanguage: {res.Language}";

            outputText.text = text;
            UiUtils.ScrollDown(scroll);

            UpdateText3D(text);
            ProcessVoiceCommand(text);

            if (!microphoneRecord.IsRecording)
            {
                microphoneRecord.StartRecord();
                buttonText.text = "Processing...";
            }
        }

        private void OnLanguageChanged(int ind)
        {
            var opt = languageDropdown.options[ind];
            whisper.language = opt.text;
        }

        private void OnTranslateChanged(bool translate)
        {
            whisper.translateToEnglish = translate;
        }

        private void OnProgressHandler(int progress)
        {
            if (!timeText)
                return;
            timeText.text = $"Progress: {progress}%";
        }

        private void OnNewSegment(WhisperSegment segment)
        {
            if (!streamSegments || !outputText)
                return;

            _buffer += segment.Text;
            outputText.text = _buffer + "...";
            UiUtils.ScrollDown(scroll);
        }

        private void UpdateText3D(string text)
        {
            if (text3D == null)
                return;

            string displayText = text;
            if (displayText.Length > 50)
            {
                displayText = displayText.Substring(0, 50) + "...";
            }

            text3D.text = displayText;
            text3D.gameObject.SetActive(true);

            if (autoHideText3D)
            {
                _text3DTimer = text3DDuration;
            }
        }

        private void ProcessVoiceCommand(string command)
        {
            command = command.ToLower().Trim();

            if (command.Contains("вперед") || command.Contains("вперёд.") ||
                command.Contains("вверх") || command.Contains("вверх.") ||
                command.Contains("верх") || command.Contains("верх.") ||
                command.Contains("прямо") || command.Contains("прямо.") ||
                command.Contains("сверху") || command.Contains("сверху."))
            {
                Debug.Log("Executing: Move Forward");
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(0, 90, 270);
                    Arrow_sign.SetActive(true);
                }
                SendCommandToESP8266("/forward");
            }
            else if (command.Contains("назад") || command.Contains("назад.") ||
                     command.Contains("вниз") || command.Contains("вниз.") ||
                     command.Contains("внизу") || command.Contains("внизу."))
            {
                Debug.Log("Executing: Move Backward");
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(0, 90, 90);
                    Arrow_sign.SetActive(true);
                }
                SendCommandToESP8266("/backward");
            }
            else if (command.Contains("влево") || command.Contains("влево.") ||
                     command.Contains("лево") || command.Contains("лево.") ||
                     command.Contains("слева") || command.Contains("слева."))
            {
                Debug.Log("Executing: Turn Left");
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(90, 0, 0);
                    Arrow_sign.SetActive(true);
                }
                SendCommandToESP8266("/left");
            }
            else if (command.Contains("вправо") || command.Contains("вправо.") ||
                     command.Contains("право") || command.Contains("право.") ||
                     command.Contains("справа") || command.Contains("справа."))
            {
                Debug.Log("Executing: Turn Right");
                if (Stop_sign != null) Stop_sign.SetActive(false);
                if (Arrow_sign != null)
                {
                    Arrow_sign.transform.localEulerAngles = new Vector3(90, 0, 180);
                    Arrow_sign.SetActive(true);
                }
                SendCommandToESP8266("/right");
            }
            else if (command.Contains("стоп") || command.Contains("стоп.") ||
                     command.Contains("остановись") || command.Contains("остановись.") ||
                     command.Contains("стой") || command.Contains("стой."))
            {
                Debug.Log("Executing: Stop");
                if (Arrow_sign != null) Arrow_sign.SetActive(false);
                if (Stop_sign != null) Stop_sign.SetActive(true);
                SendCommandToESP8266("/stop");
            }
            else if (command.Contains("разворот") || command.Contains("развернись") || command.Contains("кругом"))
            {
                Debug.Log("Executing: Turn Around");
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

        // ===== ESP8266 =====

        public void SendCommandToESP8266(string command)
        {
            _ = SendCommandAsync(command);
        }

        private async Task SendCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(esp8266IpAddress))
            {
                Debug.LogError("[ESP] IP пустой, команда не отправлена: " + command);
                return;
            }

            string url = $"http://{esp8266IpAddress}{command}";
            Debug.Log($"[ESP] sending {url}");

            try
            {
                using (var resp = await _httpClient.GetAsync(url))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    Debug.Log($"[ESP] OK {(int)resp.StatusCode} body='{body}' url={url}");
                }
            }
            catch (TaskCanceledException)
            {
                Debug.LogError($"[ESP] TIMEOUT for {url}");
            }
            catch (HttpRequestException e)
            {
                Debug.LogError($"[ESP] HTTP error for {url}: {e.Message}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ESP] FAIL for {url}: {e.GetType().Name}: {e.Message}");
            }
        }

        private void OnApplicationQuit()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(esp8266IpAddress))
                {
                    string url = $"http://{esp8266IpAddress}/stop";
                    Debug.Log($"[ESP] OnApplicationQuit sending {url}");
                    var task = _httpClient.GetAsync(url);
                    task.Wait(1000);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ESP] quit send failed: {e.Message}");
            }
        }
    }
}