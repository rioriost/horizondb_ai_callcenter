const startButton = document.getElementById("startButton");
const stopButton = document.getElementById("stopButton");
const statusText = document.getElementById("status");
const conversationText = document.getElementById("conversationId");
const partialText = document.getElementById("partial");
const finals = document.getElementById("finals");
const responses = document.getElementById("responses");

const targetSampleRate = 16000;
const BrowserAudioContext = window.AudioContext || window.webkitAudioContext;
let socket;
let mediaStream;
let audioContext;
let playbackAudioContext;
let processor;
let source;
let currentPlayback;

startButton.addEventListener("click", start);
stopButton.addEventListener("click", stop);

async function start() {
  setStatus("会話を作成しています...");
  startButton.disabled = true;

  try {
    const playbackReady = unlockAudioPlayback();
    const conversation = await createConversation();
    await playbackReady;
    conversationText.textContent = conversation.conversationId;

    mediaStream = await navigator.mediaDevices.getUserMedia({
      audio: {
        channelCount: 1,
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true
      }
    });

    socket = new WebSocket(buildAudioWebSocketUrl(conversation.conversationId));
    socket.binaryType = "arraybuffer";
    socket.addEventListener("message", handleSocketMessage);
    socket.addEventListener("close", () => setStatus("切断されました"));
    socket.addEventListener("error", () => setStatus("WebSocket エラー"));

    await waitForOpen(socket);
    socket.send(JSON.stringify({ type: "start", sampleRate: targetSampleRate }));

    audioContext = new AudioContext();
    source = audioContext.createMediaStreamSource(mediaStream);
    processor = audioContext.createScriptProcessor(4096, 1, 1);
    processor.onaudioprocess = event => {
      if (!socket || socket.readyState !== WebSocket.OPEN) {
        return;
      }

      const input = event.inputBuffer.getChannelData(0);
      const pcm = downsampleToPcm16(input, audioContext.sampleRate, targetSampleRate);
      socket.send(pcm);
    };

    source.connect(processor);
    processor.connect(audioContext.destination);

    stopButton.disabled = false;
    setStatus("接続済み。マイクに話してください。");
  } catch (error) {
    await stop();
    startButton.disabled = false;
    setStatus(`開始に失敗しました: ${error.message}`);
  }
}

async function stop() {
  stopButton.disabled = true;

  if (processor) {
    processor.disconnect();
    processor.onaudioprocess = null;
    processor = null;
  }

  if (source) {
    source.disconnect();
    source = null;
  }

  if (audioContext) {
    await audioContext.close();
    audioContext = null;
  }

  if (currentPlayback) {
    currentPlayback.stop();
    currentPlayback = null;
  }

  if (playbackAudioContext) {
    await playbackAudioContext.close();
    playbackAudioContext = null;
  }

  if (mediaStream) {
    mediaStream.getTracks().forEach(track => track.stop());
    mediaStream = null;
  }

  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ type: "stop" }));
    socket.close();
  }
  socket = null;

  startButton.disabled = false;
  setStatus("停止しました");
}

async function createConversation() {
  const response = await fetch("/api/conversations", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({})
  });

  if (!response.ok) {
    throw new Error(`conversation作成に失敗しました: ${response.status}`);
  }

  return await response.json();
}

function buildAudioWebSocketUrl(conversationId) {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${location.host}/ws/audio/conversations/${conversationId}`;
}

function waitForOpen(ws) {
  return new Promise((resolve, reject) => {
    ws.addEventListener("open", resolve, { once: true });
    ws.addEventListener("error", () => reject(new Error("WebSocket接続に失敗しました")), { once: true });
  });
}

function handleSocketMessage(event) {
  const message = JSON.parse(event.data);

  switch (message.type) {
    case "ready":
      setStatus("Azure Speech に接続しました");
      break;
    case "partial":
      partialText.textContent = message.text || "";
      partialText.classList.remove("muted");
      break;
    case "final":
      addItem(finals, message.text);
      partialText.textContent = "";
      break;
    case "response":
      addItem(responses, message.text);
      playResponseAudio(message.text);
      break;
    case "error":
      setStatus(`エラー: ${message.code || "unknown"} ${message.message || ""}`);
      break;
  }
}

function addItem(list, text) {
  if (!text) {
    return;
  }

  const item = document.createElement("li");
  item.textContent = text;
  list.prepend(item);
}

function setStatus(text) {
  statusText.textContent = text;
}

async function unlockAudioPlayback() {
  if (!BrowserAudioContext) {
    throw new Error("このブラウザは Web Audio API に対応していません。");
  }

  if (!playbackAudioContext || playbackAudioContext.state === "closed") {
    playbackAudioContext = new BrowserAudioContext();
  }

  if (playbackAudioContext.state !== "running") {
    await playbackAudioContext.resume();
  }
}

async function playResponseAudio(text) {
  if (!text) {
    return;
  }

  try {
    setStatus("応答音声を生成しています...");
    const response = await fetch("/api/speech/synthesize", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ text })
    });

    if (!response.ok) {
      throw new Error(`TTSに失敗しました: ${response.status}`);
    }

    await unlockAudioPlayback();
    const audioBuffer = await playbackAudioContext.decodeAudioData(await response.arrayBuffer());
    if (currentPlayback) {
      currentPlayback.stop();
    }

    currentPlayback = playbackAudioContext.createBufferSource();
    currentPlayback.buffer = audioBuffer;
    currentPlayback.connect(playbackAudioContext.destination);
    currentPlayback.addEventListener("ended", () => {
      currentPlayback = null;
      setStatus("接続済み。マイクに話してください。");
    }, { once: true });
    currentPlayback.start();
    setStatus("応答音声を再生しています...");
  } catch (error) {
    setStatus(`音声再生に失敗しました: ${error.message}`);
  }
}

function downsampleToPcm16(input, inputSampleRate, outputSampleRate) {
  if (outputSampleRate === inputSampleRate) {
    return floatToPcm16(input);
  }

  const ratio = inputSampleRate / outputSampleRate;
  const outputLength = Math.floor(input.length / ratio);
  const output = new Float32Array(outputLength);

  for (let i = 0; i < outputLength; i++) {
    const start = Math.floor(i * ratio);
    const end = Math.min(Math.floor((i + 1) * ratio), input.length);
    let sum = 0;
    for (let j = start; j < end; j++) {
      sum += input[j];
    }
    output[i] = sum / Math.max(1, end - start);
  }

  return floatToPcm16(output);
}

function floatToPcm16(input) {
  const buffer = new ArrayBuffer(input.length * 2);
  const view = new DataView(buffer);

  for (let i = 0; i < input.length; i++) {
    const sample = Math.max(-1, Math.min(1, input[i]));
    view.setInt16(i * 2, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true);
  }

  return buffer;
}
