// SPDX-License-Identifier: AGPL-3.0-or-later
"use strict";

const element = id => document.getElementById(id);
const status = element("status");
const connect = element("connect");
const controls = element("controls");
const talk = element("talk");
const automatic = element("automatic");
const activity = element("activity");
let token = location.hash.slice(1);
let session = null;
let held = false;

if (location.hash)
    history.replaceState(null, "", location.pathname + location.search);

if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
    status.textContent = "Microphone access requires HTTPS, or localhost for local testing.";
} else if (/^[0-9A-F]{64}$/.test(token)) {
    status.textContent = "Connect to allow this page to use your microphone.";
    connect.disabled = false;
}

function updateMode() {
    talk.disabled = automatic.checked;
    talk.setAttribute("aria-pressed", String(held));
    const mode = automatic.checked ? "auto" : held ? "hold" : "off";
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
    current.stream?.getTracks().forEach(track => track.stop());
    current.socket?.close();
    if (current.audio && current.audio.state !== "closed")
        current.audio.close().catch(() => {});
    controls.hidden = true;
    connect.hidden = false;
    connect.disabled = !token;
    automatic.checked = false;
    release();
    status.textContent = message;
}

const reconnect = "Use voice in the game console for a fresh microphone link.";

async function start() {
    if (session || !token)
        return;
    const current = { ready: false, lastSent: -Infinity };
    session = current;
    connect.disabled = true;
    status.textContent = "Waiting for microphone permission…";

    try {
        current.audio = new AudioContext();
        await current.audio.resume();
        if (session !== current)
            return;
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true }
        });
        if (session !== current) {
            stream.getTracks().forEach(track => track.stop());
            return;
        }

        current.stream = stream;
        stream.getAudioTracks()[0].addEventListener("ended", () =>
            end(current, "Microphone disconnected. " + reconnect));
        await current.audio.audioWorklet.addModule("/voice/capture.js");
        if (session !== current)
            return;

        const source = current.audio.createMediaStreamSource(stream);
        const worklet = new AudioWorkletNode(current.audio, "trauma-proximity");
        const silent = current.audio.createGain();
        silent.gain.value = 0;
        source.connect(worklet).connect(silent).connect(current.audio.destination);
        current.worklet = worklet;
        worklet.onprocessorerror = () => end(current, "Microphone processing stopped. " + reconnect);
        worklet.port.onmessage = ({ data }) => {
            const socket = current.socket;
            if (session !== current || !current.ready || (!held && !automatic.checked) ||
                socket.readyState !== WebSocket.OPEN || socket.bufferedAmount > 163 * 5 ||
                current.audio.currentTime - data.time > 0.15)
                return;
            socket.send(data.frame);
            current.lastSent = performance.now();
        };

        const address = new URL("/voice/ws", location.href);
        address.protocol = location.protocol === "https:" ? "wss:" : "ws:";
        const socket = new WebSocket(address);
        current.socket = socket;
        status.textContent = "Pairing with your game session…";
        current.timeout = setTimeout(() => end(current, "Pairing timed out. " + reconnect), 10000);
        socket.onopen = () => {
            if (session !== current)
                return;
            socket.send(token);
            token = "";
        };
        socket.onmessage = ({ data }) => {
            if (session !== current || current.ready || data !== "ready")
                return;
            clearTimeout(current.timeout);
            current.ready = true;
            connect.hidden = true;
            controls.hidden = false;
            status.textContent = "Microphone connected.";
            activity.textContent = "Idle";
            current.indicator = setInterval(() => {
                activity.textContent = performance.now() - current.lastSent < 350 ? "Transmitting" : "Idle";
            }, 200);
        };
        socket.onclose = () => end(current, "Voice connection closed. " + reconnect);
    } catch (error) {
        const message = error.name === "NotAllowedError" ? "Microphone permission was denied." :
            "Could not connect the microphone (" + error.name + ").";
        end(current, message + (token ? " Check browser permissions and try again." : " " + reconnect));
    }
}

connect.addEventListener("click", start);
element("stop").addEventListener("click", () => {
    if (session)
        end(session, "Microphone disconnected. " + reconnect);
});
automatic.addEventListener("change", release);
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
});
window.addEventListener("pagehide", () => {
    if (session)
        end(session, "Microphone disconnected. " + reconnect);
});
