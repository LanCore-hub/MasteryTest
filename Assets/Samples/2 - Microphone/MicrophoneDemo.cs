using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    [DefaultExecutionOrder(-10000)]
    public class MicrophoneDemo : MonoBehaviour
    {
        public WhisperManager whisper;
        public MicrophoneRecord microphoneRecord;
        public bool streamSegments = true;
        public bool printLanguage = true;

        [Header("Early voice HUD")]
        public bool earlyVoiceHud = true;
        public WhisperManager hudRecognizer;
        [Min(0.1f)] public float hudCheckInterval = 0.15f;
        [Range(0.1f,0.6f)] public float hudFirstAudio = 0.3f;
        [Min(0.4f)] public float hudAudioWindow = 1.2f;
        [Range(0.001f, 0.1f)] public float voiceThreshold = 0.01f;

        [Header("Проверка без оборудования")]
        [Tooltip("Включить ДО Play: микрофон и HUD работают на экране, ESP8266 и VR не используются.")]
        public bool testWithoutHardware;
        [Tooltip("Микрофон для проверки без оборудования. Пусто — автоматически выбрать единственный физический микрофон.")]
        public string testMicrophoneName;
        private string[] _testMicDevices = Array.Empty<string>();
        private bool _showMicChoices;

        public float FirstHudMs { get; private set; } = -1f;
        public string FirstHudCommand { get; private set; } = "";
        public float CommandSentMs { get; private set; } = -1f;
        public string LastCommand { get; private set; } = "";
        public string FinalText { get; private set; } = "";
        public int CommandsThisUtterance { get; private set; }
        public bool PreviewObjectActive { get; private set; }
        public float MicrophoneLevel => microphoneRecord != null ? microphoneRecord.InputRms : 0f;
        public bool IsFinalizing => _finalizing;

        private readonly List<float> _earlyAudio = new List<float>();
        private Task _earlyTask = Task.CompletedTask;
        private float _audioSinceCheck;
        private float _speechAudioDuration;
        private float _silence;
        private float _speechStart = -1f;
        private int _captureRevision;
        private int _hudRevision;
        private bool _finalizing;
        private bool _closing;
        private bool _paused;
        private bool _ignoreRecordStop;
        private bool _testMode;
        private bool _previewVisible;
        private bool _steamVrSubscribed;
        private string _hudSymbol = "—";
        private string _status = "Загрузка моделей…";

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
            _testMode = testWithoutHardware;
            // Early inference uses a short context and runs on CPU. Avoid the
            // unstable dual-Vulkan configuration even if its Inspector flag was enabled.
            if (hudRecognizer != null && hudRecognizer != whisper)
                hudRecognizer.UseGpu = false;
            if (_testMode) PrepareHardwareFreeView();
            esp8266IpAddress = MyInfoScript != null ? MyInfoScript.esp8266IpAddress : "";

            whisper.OnNewSegment += OnNewSegment;
            whisper.OnProgress += OnProgressHandler;

            microphoneRecord.OnRecordStop += OnRecordStop;
            microphoneRecord.OnChunkReady += OnEarlyAudioChunk;
            if (earlyVoiceHud) microphoneRecord.chunksLengthSec = 0.05f;

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

            if (_testMode) return;

            // ===== SteamVR StopGrab =====
            if (stopGrabAction != null)
            {
                stopGrabAction.AddOnStateDownListener(OnStopGrabDown, inputSource);
                stopGrabAction.AddOnStateUpListener(OnStopGrabUp, inputSource);
                _steamVrSubscribed = true;
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
                    _steamVrSubscribed = true;
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
            _closing = true;
            _ignoreRecordStop = true;
            microphoneRecord.OnRecordStop -= OnRecordStop;
            microphoneRecord.OnChunkReady -= OnEarlyAudioChunk;
            whisper.OnNewSegment -= OnNewSegment;
            whisper.OnProgress -= OnProgressHandler;
            if (microphoneRecord.IsRecording) microphoneRecord.StopRecord();
            if (_steamVrSubscribed && stopGrabAction != null)
            {
                stopGrabAction.RemoveOnStateDownListener(OnStopGrabDown, inputSource);
                stopGrabAction.RemoveOnStateUpListener(OnStopGrabUp, inputSource);
            }
        }

        private async void Start()
        {
            while (!_closing && (whisper.IsLoading || (hudRecognizer != null && hudRecognizer.IsLoading))) await Task.Yield();
            if (_closing) return;
            if (!whisper.IsLoaded || (earlyVoiceHud && (hudRecognizer == null || !hudRecognizer.IsLoaded)))
            {
                _status = "Не удалось загрузить модель. Подробности в Console.";
                return;
            }
            if (Microphone.devices.Length == 0)
            {
                _status = "Микрофон не найден. Проверь микрофон Windows перед Play.";
                return;
            }
            if (_testMode)
            {
                microphoneRecord.logInputDiagnostics = true;
                _testMicDevices = Microphone.devices;
                string selected = null;
                if (!string.IsNullOrWhiteSpace(testMicrophoneName))
                {
                    foreach (string device in _testMicDevices)
                        if (device.IndexOf(testMicrophoneName, StringComparison.OrdinalIgnoreCase) >= 0) { selected = device; break; }
                }
                else
                {
                    var physical = new List<string>();
                    foreach (string device in _testMicDevices)
                    {
                        string lower = device.ToLowerInvariant();
                        if (!lower.Contains("pico") && !lower.Contains("virtual") && !lower.Contains("steam streaming") && !lower.Contains("oculus")) physical.Add(device);
                    }
                    if (physical.Count == 1) selected = physical[0];
                }
                microphoneRecord.SelectedMicDevice = selected;
                Debug.Log("[Microphone input] Available: " + string.Join(" | ", _testMicDevices) + "; selected=" + (selected ?? "System default"), this);
            }
            _status = "Готово. Произнеси короткую команду и сделай паузу.";
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

            if (useKeyboardControl && !_testMode)
            {
                HandleKeyboardInput();
            }

            // ===== Резервный опрос StopGrab =====
            if (_steamVrSubscribed && stopGrabAction != null)
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
            if (_finalizing) return;
            if (!microphoneRecord.IsRecording)
            {
                _paused = false;
                ResetEarlyAudio();
                microphoneRecord.StartRecord();
                buttonText.text = "Stop";
            }
            else
            {
                _paused = true;
                microphoneRecord.StopRecord();
                buttonText.text = "Record";
            }
        }

        private async void OnRecordStop(AudioChunk recordedAudio)
        {
            if (_ignoreRecordStop) return;
            await ProcessRecordedAudioAsync(recordedAudio);
        }

        private async Task ProcessRecordedAudioAsync(AudioChunk recordedAudio)
        {
            if (_finalizing || _closing) return;
            _finalizing = true;
            buttonText.text = "Processing...";
            _status = "Окончательное распознавание…";
            _buffer = "";
            try
            {
                // The final recording must not enter the native backend while
                // an early HUD decode is still running.
                await _earlyTask;
                if (_closing) return;
                var sw = Stopwatch.StartNew();
                var res = await whisper.GetTextAsync(PadShortAudio(recordedAudio.Data, recordedAudio.Frequency, recordedAudio.Channels), recordedAudio.Frequency, recordedAudio.Channels);
                if (_closing || res == null) return;
                FinalText = res.Result ?? "";
                long time = sw.ElapsedMilliseconds;
                float rate = recordedAudio.Length / Mathf.Max(0.001f, time * 0.001f);
                if (timeText) timeText.text = $"Time: {time} ms\nRate: {rate:F1}x\nHUD: {FirstHudMs:F0} ms";
                string display = FinalText;
                if (printLanguage) display += $"\n\nLanguage: {res.Language}";
                if (outputText)
                {
                    outputText.text = display;
                    if (scroll != null) UiUtils.ScrollDown(scroll);
                }
                if (_previewVisible)
                {
                    if (Arrow_sign != null) Arrow_sign.SetActive(false);
                    if (Stop_sign != null) Stop_sign.SetActive(false);
                    _previewVisible = false;
                }
                UpdateText3D(display);
                ProcessVoiceCommand(FinalText);
                _status = $"Текст готов. Обработка: {time} мс.";
            }
            catch (Exception e) { if (!_closing) Debug.LogException(e, this); }
            finally
            {
                _finalizing = false;
                if (!_closing && !_paused && isActiveAndEnabled && !microphoneRecord.IsRecording)
                {
                    ResetEarlyAudio();
                    microphoneRecord.StartRecord();
                    buttonText.text = "Stop";
                }
            }
        }

        private void ResetEarlyAudio()
        {
            _captureRevision++;
            _earlyAudio.Clear();
            _audioSinceCheck = 0;
            _silence = 0;
            _speechStart = -1f;
            _speechAudioDuration = 0;
        }

        private void OnEarlyAudioChunk(AudioChunk chunk)
        {
            if (_closing || _finalizing || !earlyVoiceHud || !isActiveAndEnabled || chunk.Data == null || chunk.Data.Length == 0) return;
            float seconds = (float)chunk.Data.Length / (chunk.Frequency * chunk.Channels);
            double energy = 0;
            foreach (float sample in chunk.Data) energy += sample * sample;
            float chunkRms = (float)Math.Sqrt(energy / chunk.Data.Length);
            bool audible = chunkRms >= voiceThreshold;
            if (audible)
            {
                if (_speechStart < 0)
                {
                    _speechStart = Time.realtimeSinceStartup - seconds;
                    _audioSinceCheck = 0;
                    _speechAudioDuration = 0;
                    FirstHudMs = CommandSentMs = -1;
                    FirstHudCommand = LastCommand = "";
                    CommandsThisUtterance = 0;
                    PreviewObjectActive = false;
                    _status = "Слышу речь…";
                }
                _silence = 0;
            }
            else _silence += seconds;
            _earlyAudio.AddRange(chunk.Data);
            int limit = Mathf.CeilToInt(hudAudioWindow * chunk.Frequency * chunk.Channels);
            if (_earlyAudio.Count > limit) _earlyAudio.RemoveRange(0, _earlyAudio.Count - limit);
            _audioSinceCheck += seconds;
            if (_speechStart >= 0) _speechAudioDuration += seconds;
            if (_speechStart < 0 && _silence >= 0.5f)
            {
                ResetEarlyAudio();
                _earlyAudio.AddRange(chunk.Data); // pre-roll
                return;
            }
            if (_speechStart < 0 || _speechAudioDuration + 0.0001f < hudFirstAudio || _silence > 0.55f || _audioSinceCheck + 0.0001f < hudCheckInterval ||
                !_earlyTask.IsCompleted || hudRecognizer == null || !hudRecognizer.IsLoaded) return;
            _audioSinceCheck = 0;
            _earlyTask = RecognizeEarlyAsync(_earlyAudio.ToArray(), chunk.Frequency, chunk.Channels, _captureRevision, _hudRevision, _speechStart);
        }

        private async Task RecognizeEarlyAsync(float[] data, int frequency, int channels, int capture, int revision, float speechStart)
        {
            try
            {
                var result = await hudRecognizer.GetTextAsync(PadShortAudio(data, frequency, channels), frequency, channels);
                if (_closing || !isActiveAndEnabled || capture != _captureRevision || revision != _hudRevision || result == null) return;
                if (!TryPreviewCommand(result.Result, out string command, out string label, out Vector3 rotation)) return;
                bool stop = command == "/stop";
                if (Stop_sign != null) Stop_sign.SetActive(stop);
                if (Arrow_sign != null)
                {
                    if (!stop) Arrow_sign.transform.localEulerAngles = rotation;
                    Arrow_sign.SetActive(!stop);
                }
                _hudSymbol = Symbol(command);
                UpdateText3D(label + " (предварительно)");
                _previewVisible = true;
                if (FirstHudMs < 0)
                {
                    FirstHudMs = (Time.realtimeSinceStartup - speechStart) * 1000f;
                    FirstHudCommand = command;
                    PreviewObjectActive = stop ? Stop_sign != null && Stop_sign.activeInHierarchy : Arrow_sign != null && Arrow_sign.activeInHierarchy;
                    Debug.Log($"[Voice HUD] {label}: {FirstHudMs:F0} ms from speech onset", this);
                }
            }
            catch (Exception e) { if (!_closing) Debug.LogWarning("[Voice HUD] " + e.Message, this); }
        }

        private static bool TryPreviewCommand(string text, out string command, out string label, out Vector3 rotation)
        {
            command = label = null;
            rotation = Vector3.zero;
            string normalized = Regex.Replace((text ?? "").ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+", " ").Trim();
            string[] words = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return false;
            for (int i = 1; i < words.Length; i++)
                if (!words[0].StartsWith(words[i], StringComparison.Ordinal)) return false;
            switch (words[0])
            {
                case "вперед": case "вверх": case "верх": case "прямо": case "сверху":
                    command = "/forward"; label = "ВПЕРЁД"; rotation = new Vector3(0,90,270); break;
                case "назад": case "вниз": case "внизу":
                    command = "/backward"; label = "НАЗАД"; rotation = new Vector3(0,90,90); break;
                case "влево": case "лево": case "слева":
                    command = "/left"; label = "ВЛЕВО"; rotation = new Vector3(90,0,0); break;
                case "вправо": case "вправа": case "право": case "справа":
                    command = "/right"; label = "ВПРАВО"; rotation = new Vector3(90,0,180); break;
                case "стоп": case "стой": case "остановись": command = "/stop"; label = "СТОП"; break;
                default: return false;
            }
            return true;
        }

        private static float[] PadShortAudio(float[] data, int frequency, int channels)
        {
            int minimum = Mathf.CeilToInt(frequency * channels * 1.1f);
            if (data.Length >= minimum) return data;
            var padded = new float[minimum];
            Array.Copy(data, padded, data.Length);
            return padded;
        }

        private static string Symbol(string command)
        {
            return command == "/forward" ? "↑" : command == "/backward" ? "↓" : command == "/left" ? "←" :
                command == "/right" ? "→" : command == "/stop" ? "СТОП" : "—";
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
            _hudRevision++; // Later manual/final commands supersede a preview already in flight.
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
            command = command.ToLowerInvariant().Replace('ё', 'е').Trim();

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
            else if (command.Contains("вправо") || command.Contains("вправа") || command.Contains("вправо.") ||
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

        private void PrepareHardwareFreeView()
        {
            Camera source = null;
            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                if (root.name != "Player") continue;
                source = root.GetComponentInChildren<Camera>(true);
                var targets = new List<Transform>();
                if (Arrow_sign != null) targets.Add(Arrow_sign.transform);
                if (Stop_sign != null) targets.Add(Stop_sign.transform);
                if (text3D != null) targets.Add(text3D.transform);
                if (outputText != null)
                {
                    var canvas = outputText.GetComponentInParent<Canvas>();
                    if (canvas != null) targets.Add(canvas.transform);
                }
                foreach (var target in targets)
                    if (target.IsChildOf(root.transform)) target.SetParent(null, true);
                if (transform.IsChildOf(root.transform)) transform.SetParent(null, true);
                root.SetActive(false);
            }
            var cameraObject = new GameObject("Microphone test camera (runtime)");
            var camera = cameraObject.AddComponent<Camera>();
            if (source != null)
            {
                camera.CopyFrom(source);
                camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            }
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.targetTexture = null;
            cameraObject.AddComponent<AudioListener>();
            foreach (var component in FindObjectsOfType<MonoBehaviour>(true))
                if (component.GetType().Name == "MJPEGStreamRenderer") component.enabled = false;
            Application.runInBackground = true;
        }

        private void SelectTestMicrophone(string device)
        {
            if (_finalizing) { _status = "Дождись окончания распознавания, затем выбери микрофон."; return; }
            _ignoreRecordStop = true;
            try { if (microphoneRecord.IsRecording) microphoneRecord.StopRecord(); }
            finally { _ignoreRecordStop = false; }
            ResetEarlyAudio();
            _hudRevision++;
            microphoneRecord.SelectedMicDevice = device;
            testMicrophoneName = device ?? "";
            _paused = false;
            microphoneRecord.StartRecord();
            _status = string.IsNullOrEmpty(microphoneRecord.CaptureError) ? "Микрофон переключён. Произнеси команду." : microphoneRecord.CaptureError;
        }

        private void OnGUI()
        {
            if (!_testMode) return;
            Matrix4x4 saved = GUI.matrix;
            float scale = Mathf.Min(Screen.width / 1000f, Screen.height / 760f);
            GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - 1000f * scale) / 2f, (Screen.height - 760f * scale) / 2f, 0), Quaternion.identity, Vector3.one * scale);
            var text = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true };
            var symbol = new GUIStyle(text) { fontSize = 90, alignment = TextAnchor.MiddleCenter };
            GUI.Box(new Rect(0,0,1000,760), "");
            GUI.Label(new Rect(30,20,940,40), "Основная сцена — проверка без оборудования", text);
            string status = !string.IsNullOrEmpty(microphoneRecord.CaptureError) ? microphoneRecord.CaptureError : _status;
            GUI.Label(new Rect(30,65,940,45), status, text);
            string device = microphoneRecord.IsRecording ? microphoneRecord.RecordStartMicDevice : microphoneRecord.SelectedMicDevice;
            if (GUI.Button(new Rect(30,115,940,38), "Микрофон: " + (device ?? "Системный по умолчанию") + "  ▼"))
            {
                _testMicDevices = Microphone.devices;
                _showMicChoices = !_showMicChoices;
            }
            string capture = microphoneRecord.IsRecording ? "запись идёт" : _finalizing ? "обработка фразы" : "запись остановлена";
            GUI.Label(new Rect(30,163,940,35), $"Уровень: {MicrophoneLevel:F6}   Пик: {microphoneRecord.InputPeak:F6}   {capture}", text);
            GUI.Label(new Rect(350,205,300,120), _hudSymbol, symbol);
            GUI.Label(new Rect(30,335,940,55), FirstHudMs >= 0 ? $"Предварительный HUD: {FirstHudMs:F0} мс от начала речи" : "HUD: ожидаю короткую команду…", text);
            GUI.Label(new Rect(30,405,940,65), "Окончательный текст: " + FinalText, text);
            GUI.Label(new Rect(30,480,940,65), CommandSentMs >= 0 ? $"Команда {LastCommand}: {CommandSentMs:F0} мс. ESP8266 не вызывается." : "Команда роботу ещё не подтверждена.", text);
            GUI.Label(new Rect(30,565,940,30), $"Порог обнаружения речи: {voiceThreshold:F4}", text);
            voiceThreshold = GUI.HorizontalSlider(new Rect(30,610,500,25), voiceThreshold, 0.001f, 0.05f);
            if (GUI.Button(new Rect(30,675,300,45), _paused ? "Продолжить запись" : "Завершить фразу / пауза")) OnButtonPressed();
            GUI.Label(new Rect(360,665,610,60), "Скажи: вперёд, назад, влево, вправо, стоп.\nПосле слова сделай паузу.", text);
            if (_showMicChoices)
            {
                GUI.Box(new Rect(30,154,940,42 * (_testMicDevices.Length + 1) + 10), "");
                if (GUI.Button(new Rect(35,159,930,36), "Системный по умолчанию")) { SelectTestMicrophone(null); _showMicChoices = false; }
                for (int i = 0; i < _testMicDevices.Length; i++)
                    if (GUI.Button(new Rect(35,201 + 42 * i,930,36), _testMicDevices[i])) { SelectTestMicrophone(_testMicDevices[i]); _showMicChoices = false; }
            }
            GUI.matrix = saved;
        }

        // ===== ESP8266 =====

        public void SendCommandToESP8266(string command)
        {
            LastCommand = command;
            CommandsThisUtterance++;
            CommandSentMs = _speechStart >= 0 ? (Time.realtimeSinceStartup - _speechStart) * 1000f : -1;
            _hudSymbol = Symbol(command);
            Debug.Log($"[Voice command] {command}: HUD={FirstHudMs:F0} ms, dispatch={CommandSentMs:F0} ms, test={_testMode}", this);
            if (_testMode) return;
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
            _closing = true;
            if (_testMode) return;
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