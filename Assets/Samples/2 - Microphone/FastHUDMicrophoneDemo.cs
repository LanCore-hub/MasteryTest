using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Valve.VR;
using Whisper.Utils;
using Debug = UnityEngine.Debug;

namespace Whisper.Samples
{
    public class FastHUDMicrophoneDemo : MonoBehaviour
    {
        public WhisperManager whisper;
        public WhisperManager smallWhisper;
        public MicrophoneRecord microphoneRecord;
        public bool testModeWithoutVR = true;

        [Header("Early recognition")]
        [Tooltip("Use 0.8 for more context; try 0.3-0.4 for a shorter response.")]
        public float earlyBufferSec = 0.35f;
        public float earlyRepeatSec = 0.15f;
        public float earlyWindowSec = 1.2f;

        [Header("Phrase detection")]
        [Tooltip("Energy VAD on the same Whisper microphone chunks. Tune for your mic/noise.")]
        public float speechRmsThreshold = 0.008f;
        public float silenceStopSec = 0.35f;
        public float phraseTimeoutSec = 4f;

        [Header("UI (optional)")]
        public Button button;
        public Text buttonText;
        public Text outputText;
        public Text timeText;

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
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        [Header("Keyboard Control")]
        public bool useKeyboardControl = true;

        [Header("SteamVR Stop Action")]
        public SteamVR_Action_Boolean stopGrabAction;
        public SteamVR_Input_Sources inputSource = SteamVR_Input_Sources.Any;
        public bool stopOncePerPress = true;
        public bool debugSteamVR;
        private bool _gripPressed;
        private Coroutine _turnAround;

        private class Phrase
        {
            public int Id;
            public int CancelVersion;
            public float Started;
            public float LastVoice;
            public float LastEarly;
            public bool Cancelled;
            public bool Finished;
            public bool Executed;
            public readonly List<float> Samples = new List<float>();
            public Task EarlyTask = Task.CompletedTask;
        }

        private Phrase _phrase;
        private Phrase _pendingFinal;
        private int _phraseId;
        private int _cancelVersion;
        private int _lifetime;
        private bool _ready;
        private bool _listening;
        private bool _smallBusy;
        private bool _mediumBusy;
        private int _frequency;
        private int _channels;
        private float[] _preRoll = Array.Empty<float>();
        private string _shownCommand;

        private void Awake()
        {
            if (MyInfoScript != null) esp8266IpAddress = MyInfoScript.esp8266IpAddress;
            if (button != null) button.onClick.AddListener(OnButtonPressed);
            if (!testModeWithoutVR)
            {
                if (stopGrabAction == null) stopGrabAction = SteamVR_Actions.default_StopGrab;
                if (stopGrabAction != null)
                {
                    stopGrabAction.AddOnStateDownListener(OnStopGrabDown, inputSource);
                    stopGrabAction.AddOnStateUpListener(OnStopGrabUp, inputSource);
                }
            }
        }

        private IEnumerator Start()
        {
            if (whisper == null || smallWhisper == null || microphoneRecord == null ||
                whisper == smallWhisper)
            {
                Debug.LogError("[FastHUD] Assign separate Medium, Small and MicrophoneRecord components.");
                yield break;
            }

            microphoneRecord.OnChunkReady += OnChunkReady;
            microphoneRecord.OnRecordStop += OnRecordStop;

            if (!smallWhisper.IsLoaded && !smallWhisper.IsLoading) _ = smallWhisper.InitModel();
            if (!whisper.IsLoaded && !whisper.IsLoading) _ = whisper.InitModel();
            while (smallWhisper.IsLoading || whisper.IsLoading) yield return null;
            if (!smallWhisper.IsLoaded || !whisper.IsLoaded)
            {
                Debug.LogError("[FastHUD] Model loading failed. Check both .bin paths in StreamingAssets/Whisper.");
                yield break;
            }

            var warmup = WarmUp();
            while (!warmup.IsCompleted) yield return null;
            if (warmup.IsFaulted)
            {
                Debug.LogError("[FastHUD] Warm-up failed: " + warmup.Exception);
                yield break;
            }
            _ready = true;
            StartListening();
        }

        private async Task WarmUp()
        {
            var silence = new float[17600];
            await smallWhisper.GetTextAsync(silence, 16000, 1);
            await whisper.GetTextAsync(silence, 16000, 1);
            Debug.Log("[FastHUD] Small + Medium loaded and warmed up.");
        }

        private void StartListening()
        {
            if (!_ready || !isActiveAndEnabled) return;
            _listening = true;
            microphoneRecord.StartRecord();
            if (buttonText != null) buttonText.text = "Stop";
            Debug.Log($"[FastHUD] Recording started t={Time.realtimeSinceStartup:F3}s; test={testModeWithoutVR}");
        }

        private void OnButtonPressed()
        {
            if (!_ready) return;
            if (!_listening) { StartListening(); return; }
            _listening = false;
            CancelPendingCommand();
            _phrase = null;
            _preRoll = Array.Empty<float>();
            microphoneRecord.StopRecord();
            if (buttonText != null) buttonText.text = "Record";
        }

        private void OnRecordStop(AudioChunk recordedAudio)
        {
            if (_listening && _phrase != null) FinishPhrase();
        }

        private void Update()
        {
            if (_listening && !microphoneRecord.IsRecording) StartListening();
            if (autoHideText3D && text3D != null && text3D.gameObject.activeSelf)
            {
                _text3DTimer -= Time.deltaTime;
                if (_text3DTimer <= 0) text3D.gameObject.SetActive(false);
            }
            if (useKeyboardControl) HandleKeyboardInput();
            if (!testModeWithoutVR && stopGrabAction != null)
            {
                if (stopGrabAction.GetStateDown(inputSource)) HandleStopGrabDown();
                if (stopGrabAction.GetStateUp(inputSource)) _gripPressed = false;
            }
        }

        private void OnChunkReady(AudioChunk chunk)
        {
            if (!_listening || chunk.Data == null || chunk.Data.Length == 0) return;
            _frequency = chunk.Frequency;
            _channels = chunk.Channels;
            double energy = 0;
            foreach (var sample in chunk.Data) energy += sample * sample;
            bool voice = Math.Sqrt(energy / chunk.Data.Length) >= speechRmsThreshold;
            float now = Time.realtimeSinceStartup;
            if (_phrase == null)
            {
                if (!voice) { _preRoll = chunk.Data; return; }
                _phrase = new Phrase
                {
                    Id = ++_phraseId, CancelVersion = _cancelVersion,
                    Started = now - chunk.Length, LastVoice = now,
                    LastEarly = float.NegativeInfinity
                };
                _phrase.Samples.AddRange(_preRoll);
                _preRoll = Array.Empty<float>();
                HideHUDArrows();
                Debug.Log($"[FastHUD] Speech #{_phrase.Id} started t={_phrase.Started:F3}s");
            }
            _phrase.Samples.AddRange(chunk.Data);
            if (voice) _phrase.LastVoice = now;
            var duration = (float)_phrase.Samples.Count / (_frequency * _channels);
            if (!_smallBusy && !_phrase.Cancelled && duration >= earlyBufferSec &&
                now - _phrase.LastEarly >= earlyRepeatSec)
            {
                _phrase.LastEarly = now;
                _phrase.EarlyTask = RecognizeEarly(_phrase, GetCurrentChunk(_phrase), _lifetime);
            }
            if (now - _phrase.LastVoice >= silenceStopSec || duration >= phraseTimeoutSec)
                FinishPhrase();
        }

        private AudioChunk GetCurrentChunk(Phrase phrase)
        {
            int count = Math.Min(phrase.Samples.Count,
                (int)(earlyWindowSec * _frequency) * _channels);
            return new AudioChunk
            {
                Data = phrase.Samples.GetRange(phrase.Samples.Count - count, count).ToArray(),
                Frequency = _frequency, Channels = _channels,
                Length = (float)count / (_frequency * _channels)
            };
        }

        private bool IsCurrent(Phrase phrase, int lifetime)
        {
            return this != null && isActiveAndEnabled && _listening && lifetime == _lifetime &&
                phrase.Id == _phraseId && !phrase.Cancelled && phrase.CancelVersion == _cancelVersion;
        }

        private async Task RecognizeEarly(Phrase phrase, AudioChunk audio, int lifetime)
        {
            _smallBusy = true;
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await smallWhisper.GetTextAsync(PadAudio(audio.Data, audio.Frequency, audio.Channels),
                    audio.Frequency, audio.Channels);
                if (!IsCurrent(phrase, lifetime) || phrase.Executed) return;
                string text = res == null ? "" : res.Result;
                Debug.Log($"[FastHUD] Small #{phrase.Id}: {sw.ElapsedMilliseconds} ms; text='{text}'; " +
                    $"since speech={(Time.realtimeSinceStartup - phrase.Started) * 1000:F0} ms");
                if (outputText != null) outputText.text = text + " ...";
                if (timeText != null) timeText.text = $"Small: {sw.ElapsedMilliseconds} ms";
                string command = GetCommand(text);
                if (command == "/stop") { CancelPendingCommand(); return; }
                if (command != null) ShowHUDArrows(command);
            }
            catch (Exception e) { Debug.LogError("[FastHUD] Small: " + e.Message); }
            finally { _smallBusy = false; }
        }

        private void FinishPhrase()
        {
            var phrase = _phrase;
            _phrase = null;
            phrase.Finished = true;
            if (phrase.Cancelled) return;
            Debug.Log($"[FastHUD] Phrase #{phrase.Id} ended (silence/timeout/device stop).");
            _pendingFinal = phrase;
            if (!_mediumBusy) _ = RecognizeFinal(_lifetime);
        }

        private async Task RecognizeFinal(int lifetime)
        {
            _mediumBusy = true;
            try
            {
                while (_pendingFinal != null && lifetime == _lifetime)
                {
                    var phrase = _pendingFinal;
                    _pendingFinal = null;
                    if (!IsCurrent(phrase, lifetime)) continue;
                    var sw = Stopwatch.StartNew();
                    var res = await whisper.GetTextAsync(PadAudio(phrase.Samples.ToArray(), _frequency, _channels),
                        _frequency, _channels);
                    await phrase.EarlyTask;
                    if (!IsCurrent(phrase, lifetime)) continue;
                    string text = res == null ? "" : res.Result;
                    string command = GetCommand(text);
                    Debug.Log($"[FastHUD] Medium #{phrase.Id}: {sw.ElapsedMilliseconds} ms; text='{text}'");
                    if (outputText != null) outputText.text = text;
                    if (command == "/stop") { CancelPendingCommand(); continue; }
                    if (command == null) { HideHUDArrows(); continue; }
                    ShowHUDArrows(command);
                    phrase.Executed = true;
                    ExecuteRobotCommand(command);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[FastHUD] Medium: " + e.Message);
                if (this != null) HideHUDArrows();
            }
            finally
            {
                _mediumBusy = false;
                if (this != null && _listening && _pendingFinal != null)
                    _ = RecognizeFinal(_lifetime);
            }
        }

        private static float[] PadAudio(float[] samples, int frequency, int channels)
        {
            int minimum = (int)(frequency * 1.1f) * channels;
            if (samples.Length >= minimum) return samples;
            var padded = new float[minimum];
            Array.Copy(samples, padded, samples.Length);
            return padded;
        }

        public void CancelPendingCommand()
        {
            ++_cancelVersion;
            if (_phrase != null) _phrase.Cancelled = true;
            _pendingFinal = null;
            if (_turnAround != null) { StopCoroutine(_turnAround); _turnAround = null; }
            HideHUDArrows();
            if (!testModeWithoutVR && Stop_sign != null) Stop_sign.SetActive(true);
            UpdateText3D("СТОП");
            Debug.Log($"[FastHUD] /stop at t={Time.realtimeSinceStartup:F3}s; pending commands cancelled.");
            ExecuteRobotCommand("/stop");
        }

        private static string GetCommand(string text)
        {
            string normalized = (text ?? "").ToLowerInvariant().Replace('ё', 'е');
            var words = new HashSet<string>(Regex.Split(normalized, @"[^\p{L}]+"));
            if (words.Overlaps(new[] { "stop", "стоп", "стой", "остановись", "остановить" })) return "/stop";
            if (words.Overlaps(new[] { "не", "нет", "not", "dont" })) return null;
            string command = null;
            string[][] dictionary =
            {
                new[] { "/forward", "вперед", "вверх", "верх", "прямо", "сверху", "forward", "наверх" },
                new[] { "/backward", "назад", "вниз", "внизу", "backward", "сзади" },
                new[] { "/left", "влево", "налево", "лево", "слева", "left" },
                new[] { "/right", "вправо", "направо", "право", "справа", "right" },
                new[] { "/turnaround", "разворот", "развернись", "кругом" }
            };
            foreach (var entry in dictionary)
            {
                bool found = false;
                for (int i = 1; i < entry.Length; i++) found |= words.Contains(entry[i]);
                if (!found) continue;
                if (command != null) return null;
                command = entry[0];
            }
            return command;
        }

        public void ShowHUDArrows(string command)
        {
            if (_shownCommand == command) return;
            _shownCommand = command;
            Debug.Log($"[FastHUD] HUD {command} t={Time.realtimeSinceStartup:F3}s" +
                (testModeWithoutVR ? " (emulated)" : ""));
            if (testModeWithoutVR) return;
            if (Stop_sign != null) Stop_sign.SetActive(false);
            if (Arrow_sign != null)
            {
                switch (command)
                {
                    case "/forward": Arrow_sign.transform.localEulerAngles = new Vector3(0, 90, 270); UpdateText3D("Вперёд"); break;
                    case "/backward": Arrow_sign.transform.localEulerAngles = new Vector3(0, 90, 90); UpdateText3D("Назад"); break;
                    case "/left": Arrow_sign.transform.localEulerAngles = new Vector3(90, 0, 0); UpdateText3D("Влево"); break;
                    case "/right": Arrow_sign.transform.localEulerAngles = new Vector3(90, 0, 180); UpdateText3D("Вправо"); break;
                    case "/turnaround": Arrow_sign.transform.localEulerAngles = new Vector3(90, 0, 0); UpdateText3D("Разворот"); break;
                }
                Arrow_sign.SetActive(true);
            }
        }

        public void HideHUDArrows()
        {
            _shownCommand = null;
            Debug.Log("[FastHUD] HUD hidden" + (testModeWithoutVR ? " (emulated)" : " (HUD hook)"));
            if (testModeWithoutVR) return;
            if (Arrow_sign != null) Arrow_sign.SetActive(false);
            if (Stop_sign != null) Stop_sign.SetActive(false);
        }

        public void ExecuteRobotCommand(string command)
        {
            Debug.Log($"[FastHUD] Robot command {command} t={Time.realtimeSinceStartup:F3}s" +
                (testModeWithoutVR ? " (emulated)" : ""));
            if (testModeWithoutVR) return;
            if (command == "/turnaround")
            {
                if (_turnAround != null) StopCoroutine(_turnAround);
                _turnAround = StartCoroutine(TurnAround(_cancelVersion));
                return;
            }
            SendCommandToESP8266(command);
        }

        private IEnumerator TurnAround(int version)
        {
            SendCommandToESP8266("/left");
            yield return new WaitForSeconds(0.5f);
            if (version != _cancelVersion) yield break;
            SendCommandToESP8266("/left");
            yield return new WaitForSeconds(0.5f);
            if (version != _cancelVersion) yield break;
            SendCommandToESP8266("/stop");
            _turnAround = null;
        }

        public void SendCommandToESP8266(string command)
        {
            if (testModeWithoutVR)
            {
                Debug.Log("[FastHUD] ESP command (emulated): " + command);
                return;
            }
            _ = SendCommandAsync(command);
        }

        private async Task SendCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(esp8266IpAddress))
            {
                Debug.LogError("[FastHUD] ESP IP is empty. Assign MyInfoScript. Command: " + command);
                return;
            }
            string url = $"http://{esp8266IpAddress}{command}";
            Debug.Log($"[FastHUD] ESP sending {url}");
            try
            {
                using (var resp = await _httpClient.GetAsync(url))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    Debug.Log($"[FastHUD] ESP {(int)resp.StatusCode} body='{body}' url={url}");
                }
            }
            catch (TaskCanceledException) { Debug.LogError("[FastHUD] ESP TIMEOUT: " + url); }
            catch (HttpRequestException e) { Debug.LogError("[FastHUD] ESP HTTP error: " + e.Message); }
            catch (Exception e) { Debug.LogError("[FastHUD] ESP error: " + e.Message); }
        }

        private void UpdateText3D(string text)
        {
            if (testModeWithoutVR || text3D == null) return;
            text3D.text = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
            text3D.gameObject.SetActive(true);
            _text3DTimer = text3DDuration;
        }

        private void HandleKeyboardInput()
        {
            if (Input.GetKeyDown(KeyCode.Space)) { CancelPendingCommand(); return; }
            string command = null;
            if (Input.GetKeyDown(KeyCode.UpArrow)) command = "/forward";
            else if (Input.GetKeyDown(KeyCode.DownArrow)) command = "/backward";
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) command = "/left";
            else if (Input.GetKeyDown(KeyCode.RightArrow)) command = "/right";
            if (command == null) return;
            ++_cancelVersion;
            if (_phrase != null) _phrase.Cancelled = true;
            _pendingFinal = null;
            if (_turnAround != null) { StopCoroutine(_turnAround); _turnAround = null; }
            ShowHUDArrows(command);
            ExecuteRobotCommand(command);
        }

        private void OnStopGrabDown(SteamVR_Action_Boolean action, SteamVR_Input_Sources source)
        {
            HandleStopGrabDown();
        }

        private void OnStopGrabUp(SteamVR_Action_Boolean action, SteamVR_Input_Sources source)
        {
            _gripPressed = false;
        }

        private void HandleStopGrabDown()
        {
            if (!isActiveAndEnabled || testModeWithoutVR || (stopOncePerPress && _gripPressed)) return;
            _gripPressed = true;
            if (debugSteamVR) Debug.Log("[FastHUD] SteamVR StopGrab");
            CancelPendingCommand();
        }

        private void OnApplicationQuit()
        {
            if (testModeWithoutVR || string.IsNullOrWhiteSpace(esp8266IpAddress)) return;
            try { _httpClient.GetAsync($"http://{esp8266IpAddress}/stop").Wait(1000); }
            catch (Exception e) { Debug.LogWarning("[FastHUD] ESP quit send failed: " + e.Message); }
        }

        private void OnDisable()
        {
            _listening = false;
            ++_lifetime;
            ++_cancelVersion;
            _phrase = null;
            _pendingFinal = null;
            _preRoll = Array.Empty<float>();
            StopAllCoroutines();
            if (microphoneRecord != null && microphoneRecord.IsRecording) microphoneRecord.StopRecord();
        }

        private void OnDestroy()
        {
            if (stopGrabAction != null && !testModeWithoutVR)
            {
                stopGrabAction.RemoveOnStateDownListener(OnStopGrabDown, inputSource);
                stopGrabAction.RemoveOnStateUpListener(OnStopGrabUp, inputSource);
            }
            if (microphoneRecord != null)
            {
                microphoneRecord.OnChunkReady -= OnChunkReady;
                microphoneRecord.OnRecordStop -= OnRecordStop;
            }
            if (button != null) button.onClick.RemoveListener(OnButtonPressed);
        }
    }
}
