// Run the pinned local SCNet backend on planar little-endian stereo PCM.
import { readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const [input, output, rate, dependencies, weights] = process.argv.slice(2);
const require = createRequire(join(dependencies, "package.json"));
const { default: separate } = await import(
  pathToFileURL(require.resolve("@audio/neural-separate"))
);
const bytes = await readFile(input);
const pcm = new Float32Array(
  bytes.buffer,
  bytes.byteOffset,
  bytes.byteLength / 4,
);
const length = pcm.length / 2;
const channels = [pcm.subarray(0, length), pcm.subarray(length)];
const { stems } = await separate(channels, {
  sampleRate: Number(rate),
  model: "scnet",
  weights,
  device: "node",
  step: 0.5,
  progress: ({ chunk, totalChunks }) => {
    if (chunk % 20 === 0 || chunk === totalChunks) {
      console.error(`SCNet ${chunk}/${totalChunks} chunks`);
    }
  },
});
for (const name of ["drums", "bass", "other", "vocals"]) {
  const [left, right] = stems[name];
  if (left.length !== length || right.length !== length) {
    throw new Error(`SCNet changed the sample count for ${name}`);
  }
  const mono = new Float32Array(length);
  for (let i = 0; i < length; i++) mono[i] = (left[i] + right[i]) / 2;
  await writeFile(join(output, `${name}.f32`), mono);
}
