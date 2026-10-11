// Plays the samples from the app's sound helper (48 kHz stereo, interleaved) into the shared stream.
// Keeps a small cushion so uneven delivery doesn't crackle, and drops the oldest sound if it falls behind.
const CUSHION = 2400;   // frames, 50 ms
const LIMIT = 9600;     // frames, 200 ms

class Feed extends AudioWorkletProcessor {
  constructor() {
    super();
    this.queue = [];
    this.offset = 0;      // read position in queue[0], in samples
    this.frames = 0;      // frames waiting to play
    this.playing = false;
    this.port.onmessage = e => {
      const chunk = new Float32Array(e.data);
      this.queue.push(chunk);
      this.frames += chunk.length / 2;
      while (this.frames > LIMIT && this.queue.length > 1) {
        const old = this.queue.shift();
        this.frames -= (old.length - this.offset) / 2;
        this.offset = 0;
      }
    };
  }

  process(inputs, outputs) {
    const [left, right] = outputs[0];
    if (!this.playing && this.frames < CUSHION) return true;   // outputs start out silent
    this.playing = true;
    for (let i = 0; i < left.length; i++) {
      const chunk = this.queue[0];
      if (!chunk) { this.playing = false; break; }             // ran dry: build the cushion again
      left[i] = chunk[this.offset];
      right[i] = chunk[this.offset + 1];
      this.offset += 2;
      this.frames--;
      if (this.offset >= chunk.length) { this.queue.shift(); this.offset = 0; }
    }
    return true;
  }
}

registerProcessor('feed', Feed);
