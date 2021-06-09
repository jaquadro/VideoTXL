﻿
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components.Video;
using VRC.SDK3.Video.Components.AVPro;
using VRC.SDK3.Video.Components.Base;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

namespace VideoTXL
{
    [AddComponentMenu("VideoTXL/Sync Player")]
    public class SyncPlayer : UdonSharpBehaviour
    {
        public VideoPlayerProxy dataProxy;

        [Tooltip("Optional component to control and synchronize player video screens and materials")]
        public ScreenManager screenManager;
        [Tooltip("Optional component to control and synchronize player audio sources")]
        public VolumeController audioManager;
        //[Tooltip("Optional component to start or stop player based on common trigger events")]
        //public TriggerManager triggerManager;
        [Tooltip("Optional component to control access to player controls based on player type or whitelist")]
        public AccessControl accessControl;

        [Tooltip("AVPro video player component")]
        public VRCAVProVideoPlayer avProVideo;

        [Tooltip("Optional default URL to play on world load")]
        public VRCUrl defaultUrl;

        [Tooltip("Whether player controls are locked to master and instance owner by default")]
        public bool defaultLocked = false;

        public bool retryOnError = true;

        [Tooltip("Write out video player events to VRChat log")]
        public bool debugLogging = true;

        [Tooltip("Automatically loop track when finished")]
        public bool loop = false;

        float retryTimeout = 6;
        float syncFrequency = 5;
        float syncThreshold = 1;

        [UdonSynced]
        VRCUrl _syncUrl;
        VRCUrl _queuedUrl;

        [UdonSynced]
        int _syncVideoNumber;
        int _loadedVideoNumber;

        [UdonSynced, NonSerialized]
        public bool _syncOwnerPlaying;

        [UdonSynced, NonSerialized]
        public bool _syncOwnerPaused = false;

        [UdonSynced]
        float _syncVideoStartNetworkTime;

        [UdonSynced]
        bool _syncLocked = true;

        [UdonSynced]
        bool _syncRepeatPlaylist;

        [NonSerialized]
        public int localPlayerState = PLAYER_STATE_STOPPED;
        [NonSerialized]
        public VideoError localLastErrorCode;

        BaseVRCVideoPlayer _currentPlayer;

        float _lastVideoPosition = 0;
        float _videoTargetTime = 0;

        bool _waitForSync;
        float _lastSyncTime;
        float _playStartTime = 0;

        float _pendingLoadTime = 0;
        float _pendingPlayTime = 0;
        VRCUrl _pendingPlayUrl;

        // Realtime state

        [NonSerialized]
        public bool seekableSource;
        [NonSerialized]
        public float trackDuration;
        [NonSerialized]
        public float trackPosition;
        [NonSerialized]
        public bool locked;
        [NonSerialized]
        public bool repeatPlaylist;
        [NonSerialized]
        public VRCUrl currentUrl = VRCUrl.Empty;
        [NonSerialized]
        public VRCUrl lastUrl = VRCUrl.Empty;

        // Constants

        const int PLAYER_STATE_STOPPED = 0;
        const int PLAYER_STATE_LOADING = 1;
        const int PLAYER_STATE_PLAYING = 2;
        const int PLAYER_STATE_ERROR = 3;
        const int PLAYER_STATE_PAUSED = 4;

        const int SCREEN_MODE_NORMAL = 0;
        const int SCREEN_MODE_LOGO = 1;
        const int SCREEN_MODE_LOADING = 2;
        const int SCREEN_MODE_ERROR = 3;

        const int SCREEN_SOURCE_UNITY = 0;
        const int SCREEN_SOURCE_AVPRO = 1;

        void Start()
        {
            dataProxy._Init();

            avProVideo.Loop = false;
            avProVideo.Stop();
            _currentPlayer = avProVideo;

            _UpdatePlayerState(PLAYER_STATE_STOPPED);

            if (Networking.IsOwner(gameObject))
            {
                _syncLocked = defaultLocked;
                _syncRepeatPlaylist = loop;
                locked = _syncLocked;
                repeatPlaylist = _syncRepeatPlaylist;
                RequestSerialization();
            }

            _StartExtra();


            if (Networking.IsOwner(gameObject))
                _PlayVideo(defaultUrl);
        }

        public void _TriggerPlay()
        {
            DebugLog("Trigger play");
            if (localPlayerState == PLAYER_STATE_PLAYING || localPlayerState == PLAYER_STATE_LOADING)
                return;

            _PlayVideo(_syncUrl);
        }

        public void _TriggerStop()
        {
            DebugLog("Trigger stop");
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _StopVideo();
        }

        public void _TriggerPause()
        {
            DebugLog("Trigger pause");
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!seekableSource || (localPlayerState != PLAYER_STATE_PLAYING && localPlayerState != PLAYER_STATE_PAUSED))
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncOwnerPaused = !_syncOwnerPaused;

            if (_syncOwnerPaused) {
                _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - _currentPlayer.GetTime();
                _currentPlayer.Pause();
                _UpdatePlayerState(PLAYER_STATE_PAUSED);
            } else
                _currentPlayer.Play();

            RequestSerialization();
        }

        public void _TriggerLock()
        {
            if (!_IsAdmin())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncLocked = !_syncLocked;
            locked = _syncLocked;
            RequestSerialization();
        }

        public void _TriggerRepeatMode()
        {
            DebugLog("Trigger repeat mode");
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncRepeatPlaylist = !_syncRepeatPlaylist;
            repeatPlaylist = _syncRepeatPlaylist;
            RequestSerialization();
        }

        public void _Resync()
        {
            _ForceResync();
        }

        public void _ChangeUrl(VRCUrl url)
        {
            if (_syncLocked && !_CanTakeControl())
                return;

            _PlayVideo(url);

            _queuedUrl = VRCUrl.Empty;
        }

        public void _UpdateQueuedUrl(VRCUrl url)
        {
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _queuedUrl = url;
        }

        public void _SetTargetTime(float time)
        {
            if (_syncLocked && !_CanTakeControl())
                return;
            if (localPlayerState != PLAYER_STATE_PLAYING && localPlayerState != PLAYER_STATE_PAUSED)
                return;
            if (!seekableSource)
                return;

            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            // Allowing AVPro to set time directly to end of track appears to trigger deadlock sometimes
            float duration = _currentPlayer.GetDuration();
            if (duration - time < 1)
            {
                if (_IsUrlValid(_queuedUrl))
                {
                    SendCustomEventDelayedFrames("_PlayQueuedUrl", 1);
                    return;
                }
                else if (_syncRepeatPlaylist)
                {
                    SendCustomEventDelayedFrames("_LoopVideo", 1);
                    return;
                }
                time = duration - 1;
            }

            _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - time;
            SyncVideo();
            RequestSerialization();
        }

        void _PlayVideo(VRCUrl url)
        {
            _pendingPlayTime = 0;
            DebugLog("Play video " + url);
            bool isOwner = Networking.IsOwner(gameObject);
            if (!isOwner && !_CanTakeControl())
                return;

            if (!Utilities.IsValid(url))
                return;

            if (!_IsUrlValid(url))
                return;

            _syncUrl = url;
            _syncVideoNumber += isOwner ? 1 : 2;
            _loadedVideoNumber = _syncVideoNumber;
            _syncOwnerPlaying = false;
            _syncOwnerPaused = false;

            _syncVideoStartNetworkTime = float.MaxValue;
            RequestSerialization();

            _videoTargetTime = _ParseTimeFromUrl(url.Get());
            _UpdateLastUrl();

            // Conditional player stop to try and avoid piling on AVPro at end of track
            // and maybe triggering bad things
            bool playingState = localPlayerState == PLAYER_STATE_PLAYING || localPlayerState == PLAYER_STATE_PAUSED;
            if (playingState && _currentPlayer.IsPlaying && seekableSource)
            {
                float duration = _currentPlayer.GetDuration();
                float remaining = duration - _currentPlayer.GetTime();
                if (remaining > 2)
                    _currentPlayer.Stop();
            }

            _StartVideoLoad();
        }

        public void _LoopVideo()
        {
            _PlayVideo(_syncUrl);
        }

        public void _PlayQueuedUrl()
        {
            _PlayVideo(_queuedUrl);
            _queuedUrl = VRCUrl.Empty;
        }

        bool _IsUrlValid(VRCUrl url)
        {
            if (!Utilities.IsValid(url))
                return false;

            string urlStr = url.Get();
            if (urlStr == null || urlStr == "")
                return false;

            return true;
        }

        // Time parsing code adapted from USharpVideo project by Merlin
        float _ParseTimeFromUrl(string urlStr)
        {
            // Attempt to parse out a start time from YouTube links with t= or start=
            if (!urlStr.Contains("youtube.com/watch") && !urlStr.Contains("youtu.be/"))
                return 0;

            int tIndex = urlStr.IndexOf("?t=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("&t=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("?start=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("&start=");
            if (tIndex == -1)
                return 0;

            char[] urlArr = urlStr.ToCharArray();
            int numIdx = urlStr.IndexOf('=', tIndex) + 1;

            string intStr = "";
            while (numIdx < urlArr.Length)
            {
                char currentChar = urlArr[numIdx];
                if (!char.IsNumber(currentChar))
                    break;

                intStr += currentChar;
                ++numIdx;
            }

            if (intStr.Length == 0)
                return 0;

            int secondsCount = 0;
            if (!int.TryParse(intStr, out secondsCount))
                return 0;

            return secondsCount;
        }

        void _StartVideoLoadDelay(float delay)
        {
            _pendingLoadTime = Time.time + delay;
        }

        void _StartVideoLoad()
        {
            _pendingLoadTime = 0;
            if (_syncUrl == null || _syncUrl.Get() == "")
                return;

            DebugLog("Start video load " + _syncUrl);
            _UpdatePlayerState(PLAYER_STATE_LOADING);
            //localPlayerState = PLAYER_STATE_LOADING;

            _UpdateScreenMaterial(SCREEN_MODE_LOADING);

#if !UNITY_EDITOR
            _currentPlayer.LoadURL(_syncUrl);
#endif
        }

        public void _StopVideo()
        {
            DebugLog("Stop video");

            if (seekableSource)
                _lastVideoPosition = _currentPlayer.GetTime();

            _UpdatePlayerState(PLAYER_STATE_STOPPED);
            _UpdateScreenMaterial(SCREEN_MODE_LOGO);

            _currentPlayer.Stop();
            _videoTargetTime = 0;
            _pendingPlayTime = 0;
            _pendingLoadTime = 0;
            _playStartTime = 0;

            if (Networking.IsOwner(gameObject))
            {
                _syncVideoStartNetworkTime = 0;
                _syncOwnerPlaying = false;
                _syncOwnerPaused = false;
                _syncUrl = VRCUrl.Empty;
                RequestSerialization();
            }
        }

        public override void OnVideoReady()
        {
            float duration = _currentPlayer.GetDuration();
            DebugLog("Video ready, duration: " + duration + ", position: " + _currentPlayer.GetTime());

            _AudioStart();

            // If a seekable video is loaded it should have a positive duration.  Otherwise we assume it's a non-seekable stream
            seekableSource = !float.IsInfinity(duration) && !float.IsNaN(duration) && duration > 1;

            // If player is owner: play video
            // If Player is remote:
            //   - If owner playing state is already synced, play video
            //   - Otherwise, wait until owner playing state is synced and play later in update()
            //   TODO: Streamline by always doing this in update instead?

            if (Networking.IsOwner(gameObject))
                _currentPlayer.Play();
            else
            {
                // TODO: Stream bypass owner
                if (_syncOwnerPlaying)
                    _currentPlayer.Play();
                else
                    _waitForSync = true;
            }
        }

        public override void OnVideoStart()
        {
            DebugLog("Video start");

            if (Networking.IsOwner(gameObject))
            {
                bool paused = localPlayerState == PLAYER_STATE_PAUSED;
                if (paused)
                    _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - _currentPlayer.GetTime();
                else
                    _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - _videoTargetTime;

                _UpdatePlayerState(PLAYER_STATE_PLAYING);
                _UpdateScreenMaterial(SCREEN_MODE_NORMAL);
                _playStartTime = Time.time;

                _syncOwnerPlaying = true;
                _syncOwnerPaused = false;
                RequestSerialization();

                if (!paused)
                    _currentPlayer.SetTime(_videoTargetTime);
            }
            else
            {
                if (!_syncOwnerPlaying || _syncOwnerPaused)
                {
                    // TODO: Owner bypass
                    _currentPlayer.Pause();
                    _waitForSync = true;

                    if (_syncOwnerPaused)
                        _UpdatePlayerState(PLAYER_STATE_PAUSED);
                }
                else
                {
                    _UpdatePlayerState(PLAYER_STATE_PLAYING);
                    _UpdateScreenMaterial(SCREEN_MODE_NORMAL);
                    _playStartTime = Time.time;
                    
                    SyncVideo();
                }
            }
        }

        public override void OnVideoEnd()
        {
            if (!seekableSource && Time.time - _playStartTime < 1)
            {
                Debug.Log("Video end encountered at start of stream, ignoring");
                return;
            }

            seekableSource = false;
            dataProxy.seekableSource = false;

            _UpdatePlayerState(PLAYER_STATE_STOPPED);
            _UpdateScreenMaterial(SCREEN_MODE_LOGO);

            DebugLog("Video end");
            _lastVideoPosition = 0;

            _AudioStop();

            if (Networking.IsOwner(gameObject))
            {
                if (_IsUrlValid(_queuedUrl))
                    SendCustomEventDelayedFrames("_PlayQueuedUrl", 1);
                else if (_syncRepeatPlaylist)
                    SendCustomEventDelayedFrames("_LoopVideo", 1);
                else
                {
                    _syncVideoStartNetworkTime = 0;
                    _syncOwnerPlaying = false;
                    RequestSerialization();
                }
            }
        }

        public override void OnVideoError(VideoError videoError)
        {
            _currentPlayer.Stop();

            DebugLog("Video stream failed: " + _syncUrl);
            DebugLog("Error code: " + videoError);

            //localPlayerState = PLAYER_STATE_ERROR;
            //localLastErrorCode = videoError;
            _UpdatePlayerStateError(videoError);
            _UpdateScreenVideoError(videoError);
            _UpdateScreenMaterial(SCREEN_MODE_ERROR);
            _AudioStop();

            if (Networking.IsOwner(gameObject))
            {
                if (retryOnError)
                {
                    _StartVideoLoadDelay(retryTimeout);
                }
                else
                {
                    _syncVideoStartNetworkTime = 0;
                    _videoTargetTime = 0;
                    _syncOwnerPlaying = false;
                    RequestSerialization();
                }
            }
            else
            {
                _StartVideoLoadDelay(retryTimeout);
            }
        }

        public bool _IsAdmin()
        {
            if (_hasAccessControl)
                return accessControl._LocalHasAccess();

            VRCPlayerApi player = Networking.LocalPlayer;
            return player.isMaster || player.isInstanceOwner;
        }

        public bool _CanTakeControl()
        {
            if (_hasAccessControl)
                return !_syncLocked || accessControl._LocalHasAccess();

            VRCPlayerApi player = Networking.LocalPlayer;
            return player.isMaster || player.isInstanceOwner || !_syncLocked;
        }

        public override void OnDeserialization()
        {
            if (Networking.IsOwner(gameObject))
                return;

            DebugLog($"Deserialize: video #{_syncVideoNumber}");

            locked = _syncLocked;
            repeatPlaylist = _syncRepeatPlaylist;

            if (_syncVideoNumber == _loadedVideoNumber)
            {
                bool playingState = localPlayerState == PLAYER_STATE_PLAYING || localPlayerState == PLAYER_STATE_PAUSED;
                if (playingState && !_syncOwnerPlaying)
                    SendCustomEventDelayedFrames("_StopVideo", 1);
                else if (localPlayerState == PLAYER_STATE_PAUSED && !_syncOwnerPaused)
                {
                    DebugLog("Unpausing video");
                    _currentPlayer.Play();
                    _UpdatePlayerState(PLAYER_STATE_PLAYING);
                } else if (localPlayerState == PLAYER_STATE_PLAYING && _syncOwnerPaused)
                {
                    DebugLog("Pausing video");
                    _currentPlayer.Pause();
                    _UpdatePlayerState(PLAYER_STATE_PAUSED);
                }

                return;
            }

            // There was some code here to bypass load owner sync bla bla

            _loadedVideoNumber = _syncVideoNumber;
            _UpdateLastUrl();

            DebugLog("Starting video load from sync");

            _StartVideoLoad();
        }

        public override void OnPostSerialization(SerializationResult result)
        {
            if (!result.success)
            {
                DebugLog("Failed to sync");
                return;
            }
        }

        void Update()
        {
            bool isOwner = Networking.IsOwner(gameObject);
            float time = Time.time;

            if (_pendingPlayTime > 0 && time > _pendingPlayTime)
                _PlayVideo(_pendingPlayUrl);
            if (_pendingLoadTime > 0 && Time.time > _pendingLoadTime)
                _StartVideoLoad();

            bool playingState = localPlayerState == PLAYER_STATE_PLAYING || localPlayerState == PLAYER_STATE_PAUSED;
            if (seekableSource && playingState)
            {
                trackDuration = _currentPlayer.GetDuration();
                trackPosition = _currentPlayer.GetTime();
            }

            if (seekableSource && _syncOwnerPaused)
                _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - _currentPlayer.GetTime();


            // Video is playing: periodically sync with owner
            if (isOwner || !_waitForSync)
            {
                SyncVideoIfTime();
                return;
            }

            // Video is not playing, but still waiting for go-ahead from owner
            if (!_syncOwnerPlaying || _syncOwnerPaused)
                return;

            // Got go-ahead from owner, start playing video
            _UpdatePlayerState(PLAYER_STATE_PLAYING);

            _waitForSync = false;
            _currentPlayer.Play();

            SyncVideo();
        }

        void SyncVideoIfTime()
        {
            if (Time.realtimeSinceStartup - _lastSyncTime > syncFrequency)
            {
                _lastSyncTime = Time.realtimeSinceStartup;
                SyncVideo();
            }
        }

        void SyncVideo()
        {
            if (seekableSource)
            {
                float duration = _currentPlayer.GetDuration();
                float current = _currentPlayer.GetTime();
                float offsetTime = Mathf.Clamp((float)Networking.GetServerTimeInSeconds() - _syncVideoStartNetworkTime, 0f, duration);
                if (Mathf.Abs(current - offsetTime) > syncThreshold && (duration - current) > 2)
                    _currentPlayer.SetTime(offsetTime);
            }
        }

        public void _ForceResync()
        {
            bool isOwner = Networking.IsOwner(gameObject);
            if (isOwner)
            {
                if (seekableSource)
                {
                    float startTime = _videoTargetTime;
                    if (_currentPlayer.IsPlaying)
                        startTime = _currentPlayer.GetTime();

                    _StartVideoLoad();
                    _videoTargetTime = startTime;
                }
                return;
            }

            _currentPlayer.Stop();
            if (_syncOwnerPlaying)
                _StartVideoLoad();
        }

        void _UpdatePlayerState(int state)
        {
            localPlayerState = state;
            dataProxy.playerState = state;
            dataProxy._EmitStateUpdate();
        }

        void _UpdatePlayerStateError(VideoError error)
        {
            localPlayerState = PLAYER_STATE_ERROR;
            localLastErrorCode = error;
            dataProxy.playerState = PLAYER_STATE_ERROR;
            dataProxy.lastErrorCode = error;
            dataProxy._EmitStateUpdate();
        }

        void _UpdateLastUrl()
        {
            if (_syncUrl == currentUrl)
                return;

            lastUrl = currentUrl;
            currentUrl = _syncUrl;
        }

        // Extra

        bool _hasScreenManager = false;
        //bool _hasTriggerManager = false;
        bool _hasAudioManager = false;
        bool _hasAccessControl = false;

        void _StartExtra()
        {
            _hasScreenManager = Utilities.IsValid(screenManager);
            _hasAudioManager = Utilities.IsValid(audioManager);
            //_hasTriggerManager = Utilities.IsValid(triggerManager);
            _hasAccessControl = Utilities.IsValid(accessControl);

            _UpdateScreenSource(SCREEN_SOURCE_AVPRO);
            _UpdateScreenMaterial(SCREEN_MODE_LOGO);
        }

        void _UpdateScreenMaterial(int screenMode)
        {
            //if (_hasScreenManager)
            //    screenManager._UpdateScreenMaterial(screenMode);
        }

        void _UpdateScreenSource(int screenSource)
        {
            if (_hasScreenManager)
                screenManager._UpdateScreenSource(screenSource);
        }

        void _UpdateScreenVideoError(VideoError error)
        {
            if (_hasScreenManager)
                screenManager._UpdateVideoError(error);
        }

        void _AudioStart()
        {
            if (_hasAudioManager)
                audioManager._VideoStart();
        }

        void _AudioStop()
        {
            if (_hasAudioManager)
                audioManager._VideoStop();
        }

        // Debug

        void DebugLog(string message)
        {
            if (debugLogging)
                Debug.Log("[VideoTXL:SyncPlayer] " + message);
        }
    }
}
