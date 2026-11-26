// SPDX-License-Identifier: AGPL-3.0-or-later
"use strict";

const element = id => document.getElementById(id);
const status = element("status");
const pairing = element("pairing");
const connect = element("connect");
const controls = element("controls");
const talk = element("talk");
const automatic = element("automatic");
const activity = element("activity");
const codeInput = element("voice-code");
const codeRow = element("code-row");
const device = element("device");
const meter = element("mic-level");
const inputGain = element("input-gain");
const mute = element("mute-microphone");
const keepAwake = element("wake-lock");
const microphoneOptions = ["echo", "noise", "auto-gain"].map(element);
let token = location.hash.slice(1);
const browserKey = Array.from(crypto.getRandomValues(new Uint8Array(32)),
    byte => byte.toString(16).padStart(2, "0")).join("");
let session = null;
let held = false;
let muted = false;

if (location.hash)
    history.replaceState(null, "", location.pathname + location.search);

const validCode = value => /^(?:[0-9A-F]{20}|[0-9A-F]{64})$/.test(value);
const supported = Boolean(window.isSecureContext && navigator.mediaDevices?.getUserMedia);
if (!supported) {
    status.textContent = "Microphone access requires HTTPS, or localhost for local testing.";
} else if (validCode(token)) {
    status.textContent = "Connect to allow this page to use your microphone.";
    connect.disabled = false;
    codeRow.hidden = true;
}

codeInput.addEventListener("input", () => {
    token = codeInput.value.toUpperCase().replace(/[ -]/g, "");
    connect.disabled = !supported || !validCode(token);
});
inputGain.addEventListener("input", () => {
    element("input-gain-value").textContent = Math.round(Number(inputGain.value) * 100) + "%";
    if (session?.gain)
        session.gain.gain.value = Number(inputGain.value);
});
mute.addEventListener("click", () => {
    muted = !muted;
    mute.setAttribute("aria-pressed", String(muted));
    mute.textContent = muted ? "Unmute microphone" : "Mute microphone";
    updateMode();
});
element("wake-lock-setting").hidden = !navigator.wakeLock;
keepAwake.addEventListener("change", () => updateWakeLock(session));

async function updateWakeLock(current) {
    if (!current || !navigator.wakeLock)
        return;
    if (!keepAwake.checked || document.hidden) {
        const lock = current.wakeLock;
        current.wakeLock = null;
        lock?.release().catch(() => {});
    } else if (!current.wakeLock && !current.wakeLockPending) {
        current.wakeLockPending = true;
        try {
            const lock = await navigator.wakeLock.request("screen");
            if (session === current && keepAwake.checked && !document.hidden) {
                current.wakeLock = lock;
                lock.addEventListener("release", () => {
                    if (current.wakeLock === lock)
                        current.wakeLock = null;
                });
            } else {
                await lock.release();
            }
        } catch {
            // The browser can deny the wake lock while still allowing the microphone.
        } finally {
            current.wakeLockPending = false;
        }
    }
}

async function refreshDevices() {
    const inputs = await navigator.mediaDevices.enumerateDevices();
    const selected = device.value;
    device.length = 1;
    for (const input of inputs) {
        if (input.kind === "audioinput" && input.deviceId)
            device.add(new Option(input.label || "Microphone " + device.length, input.deviceId));
    }
    if (selected && !Array.from(device.options).some(option => option.value === selected))
        device.add(new Option("Selected microphone (unavailable)", selected));
    device.value = selected;
}

async function switchMicrophone(current, selected) {
    const request = ++current.microphoneRequest;
    const processing = microphoneOptions.map(option => option.checked);
    let stream;
    try {
        stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                channelCount: 1,
                echoCancellation: processing[0],
                noiseSuppression: processing[1],
                autoGainControl: processing[2],
                ...(selected ? { deviceId: { exact: selected } } : {})
            }
        });
        if (session !== current || request !== current.microphoneRequest)
            return;

        const source = current.audio.createMediaStreamSource(stream);
        source.connect(current.gain);
        const previous = current.stream;
        current.source?.disconnect();
        current.source = source;
        current.stream = stream;
        current.device = selected;
        current.processing = processing;
        stream = null;
        const active = current.stream;
        active.getAudioTracks()[0].addEventListener("ended", () => {
            if (current.stream !== active || session !== current)
                return;
            current.source.disconnect();
            current.source = null;
            current.stream = null;
            status.textContent = "Microphone disconnected. Choose another input to resume.";
            refreshDevices().catch(() => {});
        });
        previous?.getTracks().forEach(track => track.stop());
        if (current.ready)
            status.textContent = "Microphone connected.";
        refreshDevices().catch(() => {});
    } catch (error) {
        if (session !== current || request !== current.microphoneRequest)
            return;
        if (!current.socket)
            throw error;

        device.value = current.device ?? "";
        current.processing?.forEach((checked, index) => microphoneOptions[index].checked = checked);
        status.textContent = current.stream
            ? "Could not switch microphones. Still using the previous input."
            : "Could not open that microphone. Choose another input to resume.";
    } finally {
        stream?.getTracks().forEach(track => track.stop());
    }
}

function changeMicrophone() {
    const current = session;
    if (current?.socket)
        switchMicrophone(current, device.value).catch(() => {});
}

device.addEventListener("change", changeMicrophone);
microphoneOptions.forEach(option => option.addEventListener("change", changeMicrophone));
if (supported) {
    refreshDevices().catch(() => {});
    navigator.mediaDevices.addEventListener?.("devicechange", () => refreshDevices().catch(() => {}));
}
function updateMode() {
    talk.disabled = automatic.checked;
    talk.setAttribute("aria-pressed", String(held));
    const game = session?.gameMode;
    const mode = muted || game === 3 ? "off" : game === 1 || game === 2 || held ? "hold" :
        automatic.checked ? "auto" : "off";
    session?.worklet?.port.postMessage(mode);
}

function release() {
    held = false;
    updateMode();
}

function end(current, message) {
    if (session !== current)
        return;
    session = null;
    clearInterval(current.indicator);
    clearTimeout(current.timeout);
    clearTimeout(current.retryTimer);
    current.wakeLock?.release().catch(() => {});
    current.stream?.getTracks().forEach(track => track.stop());
    current.socket?.close();
    if (current.audio && current.audio.state !== "closed")
        current.audio.close().catch(() => {});
    controls.hidden = true;
    pairing.hidden = false;
    connect.hidden = false;
    connect.disabled = !supported || !validCode(token);
    codeRow.hidden = validCode(token);
    if (!token)
        codeInput.value = "";
    element("mode-ptt").checked = true;
    release();
    status.textContent = message;
}

const reconnect = "Use voice in the game console for a fresh microphone link.";

async function start() {
    if (session || !validCode(token))
        return;
    const current = { ready: false, lastSent: -Infinity, retries: 0, gameMode: 0, microphoneRequest: 0 };
    session = current;
    connect.disabled = true;
    status.textContent = "Waiting for microphone permission…";

    try {
        current.audio = new AudioContext();
        await current.audio.resume();
        if (session !== current)
            return;
        await current.audio.audioWorklet.addModule("/voice/capture.js");
        if (session !== current)
            return;

        current.gain = current.audio.createGain();
        current.gain.gain.value = Number(inputGain.value);
        const analyzer = current.audio.createAnalyser();
        analyzer.fftSize = 512;
        current.analyzer = analyzer;
        current.levelSamples = new Float32Array(analyzer.fftSize);
        const worklet = new AudioWorkletNode(current.audio, "trauma-proximity");
        const silent = current.audio.createGain();
        silent.gain.value = 0;
        current.gain.connect(analyzer).connect(worklet).connect(silent).connect(current.audio.destination);
        current.worklet = worklet;
        worklet.onprocessorerror = () => end(current, "Microphone processing stopped. " + reconnect);
        worklet.port.onmessage = ({ data }) => {
            const socket = current.socket;
            if (session !== current || !current.ready || !current.stream || muted || current.gameMode === 3 ||
                (!held && !automatic.checked && current.gameMode === 0) ||
                socket.readyState !== WebSocket.OPEN || socket.bufferedAmount > 163 * 5 ||
                current.audio.currentTime - data.time > 0.15)
                return;
            socket.send(data.frame);
            current.lastSent = performance.now();
        };

        await switchMicrophone(current, device.value);
        if (session === current)
            connectSocket(current);
    } catch (error) {
        const message = error.name === "NotAllowedError" ? "Microphone permission was denied." :
            "Could not connect the microphone (" + error.name + ").";
        end(current, message + " Check browser permissions and try again.");
    }
}

function connectSocket(current) {
        const address = new URL("/voice/ws", location.href);
        address.protocol = location.protocol === "https:" ? "wss:" : "ws:";
        const socket = new WebSocket(address);
        current.socket = socket;
        status.textContent = "Pairing with your game session…";
        current.timeout = setTimeout(() => {
            if (current.socket === socket)
                socket.close();
        }, 10000);
        socket.onopen = () => {
            if (session !== current)
                return;
            socket.send(token.padEnd(64, " ") + browserKey);
        };
        socket.onmessage = ({ data }) => {
            if (session !== current)
                return;
            if (typeof data === "string" && /^mode:[0-3]$/.test(data)) {
                current.gameMode = Number(data.slice(-1));
                updateMode();
                return;
            }
            if (current.ready || data !== "ready")
                return;
            clearTimeout(current.timeout);
            current.ready = true;
            current.retries = 0;
            connect.hidden = true;
            pairing.hidden = true;
            codeRow.hidden = true;
            controls.hidden = false;
            status.textContent = "Microphone connected.";
            updateWakeLock(current);
            activity.textContent = "Idle";
            current.indicator = setInterval(() => {
                activity.textContent = performance.now() - current.lastSent < 350 ? "Transmitting" : "Idle";
                current.analyzer.getFloatTimeDomainData(current.levelSamples);
                let energy = 0;
                for (const sample of current.levelSamples)
                    energy += sample * sample;
                meter.value = Math.min(1, Math.sqrt(energy / current.levelSamples.length) * 3);
            }, 75);
        };
        socket.onclose = event => {
            if (session !== current || socket !== current.socket)
                return;
            clearTimeout(current.timeout);
            clearInterval(current.indicator);
            current.ready = false;
            if (event.code === 1008) {
                token = "";
                end(current, "Microphone link expired. " + reconnect);
            } else if (current.retries < 5) {
                status.textContent = "Connection lost. Reconnecting microphone…";
                activity.textContent = "Reconnecting…";
                current.retryTimer = setTimeout(() => connectSocket(current), 500 * 2 ** current.retries++);
            } else {
                end(current, "Connection lost. Click Connect microphone to retry.");
            }
        };
}

pairing.addEventListener("submit", event => {
    event.preventDefault();
    if (supported)
        return start();
});
element("stop").addEventListener("click", () => {
    if (session)
        end(session, "Microphone disconnected. Click Connect microphone to reconnect.");
});
automatic.addEventListener("change", release);
element("mode-ptt").addEventListener("change", release);
talk.addEventListener("pointerdown", event => {
    if (event.button !== 0 || automatic.checked)
        return;
    talk.setPointerCapture(event.pointerId);
    held = true;
    updateMode();
});
talk.addEventListener("pointerup", release);
talk.addEventListener("pointercancel", release);
talk.addEventListener("lostpointercapture", release);
talk.addEventListener("keydown", event => {
    if (event.code !== "Space" && event.code !== "Enter")
        return;
    event.preventDefault();
    if (!event.repeat && !automatic.checked) {
        held = true;
        updateMode();
    }
});
talk.addEventListener("keyup", event => {
    if (event.code === "Space" || event.code === "Enter")
        release();
});
talk.addEventListener("blur", release);
window.addEventListener("blur", release);
document.addEventListener("visibilitychange", () => {
    if (document.hidden)
        release();
    updateWakeLock(session);
});
window.addEventListener("pagehide", () => {
    if (session)
        end(session, "Microphone disconnected. " + reconnect);
});
