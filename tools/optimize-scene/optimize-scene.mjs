// Optimizes a scene GLB for the dashboard viewer.
//
//   node optimize-scene.mjs <input.glb> <output.glb>
//
// Run by Scenes/Processing/NodeGltfOptimizer.cs; exits non-zero on any failure. The pipeline is
// the one `gltf-transform optimize` uses, minus every step that touches the node hierarchy,
// because replays address nodes through `extras.objectId` and animate them individually:
//
//   - no instance / flatten / join: they move meshes between nodes and delete nodes.
//   - prune keeps empty leaf nodes (tracked empty GameObjects, cameras, lights) and extras.
//   - no palette / simplify: the inspector shows materials and geometry as authored.
//   - positions are NOT quantized. `quantize` rewrites the node matrix of every mesh node to undo
//     the quantization scale (and adds child nodes under meshes that have children), and a
//     replay overwrites node position/rotation, which would displace the geometry. Everything
//     else is quantized and meshopt-compressed.
//
// Textures become KTX2 (ETC1S, UASTC for data maps like `gltf-transform optimize`) through the
// `ktx` CLI from KTX-Software, which must be on PATH. @gltf-transform/cli does not export its
// toktx helper, so the step below follows it (see the pinned version's src/transforms/toktx.ts).
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { ImageUtils, NodeIO, TextureChannel } from '@gltf-transform/core';
import { ALL_EXTENSIONS, EXTMeshoptCompression, KHRTextureBasisu } from '@gltf-transform/extensions';
import {
  dedup,
  fitWithin,
  getTextureChannelMask,
  getTextureColorSpace,
  listTextureSlots,
  prune,
  quantize,
  reorder,
  weld,
} from '@gltf-transform/functions';
import { MeshoptDecoder, MeshoptEncoder } from 'meshoptimizer';
import sharp from 'sharp';

const MAX_TEXTURE_SIZE = 2048;
// Data maps need the higher-quality UASTC; everything else is ETC1S. Same split as `optimize`.
const UASTC_SLOTS = /^(normalTexture|occlusionTexture|metallicRoughnessTexture)$/;
const { R: CHANNEL_R, G: CHANNEL_G, A: CHANNEL_A } = TextureChannel;

function log(message) {
  console.error(`optimize-scene: ${message}`);
}

function runKtx(args) {
  return new Promise((resolve, reject) => {
    const child = spawn('ktx', args, { stdio: ['ignore', 'ignore', 'pipe'] });
    let stderr = '';
    child.stderr.on('data', (chunk) => (stderr += chunk));
    child.on('error', (err) => reject(new Error(`could not run "ktx" (KTX-Software must be on PATH): ${err.message}`)));
    child.on('close', (code) => resolve({ code, stderr }));
  });
}

function ktxParams(texture, slots, isUastc, numTextures) {
  const colorSpace = getTextureColorSpace(texture);
  const channels = getTextureChannelMask(texture);
  const isNormalMap = slots.some((slot) => /normal/i.test(slot));
  const params = ['--generate-mipmap'];

  if (isUastc) {
    params.push('--encode', 'uastc', '--uastc-quality', 4);
    if (!isNormalMap) params.push('--uastc-rdo', '--uastc-rdo-l', 4);
    params.push('--zstd', 18);
  } else {
    params.push('--encode', 'basis-lz', '--qlevel', 255);
    // ETC1S's rate-distortion optimization smears normal maps; it is only used here for color.
    if (isNormalMap) params.push('--no-endpoint-rdo', '--no-selector-rdo');
  }

  if (colorSpace === 'srgb') params.push('--assign-tf', 'srgb', '--assign-primaries', 'bt709');
  else if (slots.length && !colorSpace) params.push('--assign-tf', 'linear', '--assign-primaries', 'none');

  if (channels === CHANNEL_R) params.push('--format', 'R8_UNORM');
  else if (channels === CHANNEL_G || channels === (CHANNEL_R | CHANNEL_G)) params.push('--format', 'R8G8_UNORM');
  else if (!(channels & CHANNEL_A)) params.push('--format', colorSpace === 'srgb' ? 'R8G8B8_SRGB' : 'R8G8B8_UNORM');
  else params.push('--format', colorSpace === 'srgb' ? 'R8G8B8A8_SRGB' : 'R8G8B8A8_UNORM');

  const cpus = os.cpus().length || 1;
  if (numTextures > 1) params.push('--threads', Math.max(2, Math.min(cpus, Math.round((3 * cpus) / numTextures))));
  return params;
}

/** Resizes (to at most MAX_TEXTURE_SIZE, dimensions rounded up to a multiple of four) and encodes every PNG/JPEG as KTX2. */
function ktx2() {
  return async (document) => {
    const textures = document.getRoot().listTextures();
    const work = textures.filter((t) => ['image/png', 'image/jpeg'].includes(t.getMimeType()));
    if (work.length === 0) return;

    const basisu = document.createExtension(KHRTextureBasisu).setRequired(true);
    const dir = await mkdtemp(path.join(os.tmpdir(), 'moodyblues-ktx-'));
    try {
      const jobs = Math.max(1, Math.min(os.cpus().length || 1, 4));
      let next = 0;
      const worker = async () => {
        while (next < work.length) {
          const index = next++;
          await encodeTexture(work[index], index, work.length, dir);
        }
      };
      await Promise.all(Array.from({ length: jobs }, worker));
    } finally {
      await rm(dir, { recursive: true, force: true });
    }

    if (!textures.some((t) => t.getMimeType() === 'image/ktx2')) basisu.dispose();
  };
}

async function encodeTexture(texture, index, total, dir) {
  const label = `texture ${texture.getName() || texture.getURI() || index + 1}`;
  let image = texture.getImage();
  let mimeType = texture.getMimeType();
  let size = texture.getSize();
  if (!image || !size) {
    log(`${label}: unreadable, left as is.`);
    return;
  }

  const slots = listTextureSlots(texture);
  const fitted = fitWithin(size, [MAX_TEXTURE_SIZE, MAX_TEXTURE_SIZE]).map((v) => 4 * Math.max(1, Math.ceil(v / 4)));
  if (fitted[0] !== size[0] || fitted[1] !== size[1]) {
    const resized = await sharp(image, { limitInputPixels: true })
      .toFormat('png')
      .resize(fitted[0], fitted[1], { fit: 'fill', kernel: 'lanczos3' })
      .toBuffer();
    image = new Uint8Array(resized.buffer, resized.byteOffset, resized.byteLength);
    mimeType = 'image/png';
    log(`${label}: resized ${size.join('x')} -> ${fitted.join('x')}.`);
  }

  const extension = ImageUtils.mimeTypeToExtension(mimeType);
  const src = path.join(dir, `${index}.${extension}`);
  const dst = path.join(dir, `${index}.ktx2`);
  await writeFile(src, image);

  const isUastc = slots.some((slot) => UASTC_SLOTS.test(slot));
  const args = ['create', ...ktxParams(texture, slots, isUastc, total), src, dst];
  const { code, stderr } = await runKtx(args);
  if (code !== 0) {
    throw new Error(`${label}: ktx exited ${code}: ${stderr.trim()}`);
  }

  texture.setImage(await readFile(dst)).setMimeType('image/ktx2');
  if (texture.getURI()) texture.setURI(`${texture.getURI().replace(/\.[^./]+$/, '')}.ktx2`);
}

/**
 * `meshopt({level: 'high'})` minus POSITION quantization (see the header): vertex data except
 * positions is quantized, then EXT_meshopt_compression encodes it, with its filters for normals.
 */
function meshoptWithoutPositionQuantization(encoder) {
  return async (document) => {
    if (document.getRoot().listAccessors().length === 0) return;
    await document.transform(
      reorder({ encoder, target: 'size' }),
      quantize({
        pattern: /^(TEXCOORD|JOINTS|WEIGHTS|COLOR)(_\d+)?$/,
        patternTargets: /^(POSITION|TEXCOORD|JOINTS|WEIGHTS|COLOR|NORMAL|TANGENT)(_\d+)?$/,
        quantizeNormal: 8,
      }),
    );
    document
      .createExtension(EXTMeshoptCompression)
      .setRequired(true)
      .setEncoderOptions({ method: EXTMeshoptCompression.EncoderMethod.FILTER });
  };
}

async function main() {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) {
    throw new Error('usage: node optimize-scene.mjs <input.glb> <output.glb>');
  }

  await Promise.all([MeshoptDecoder.ready, MeshoptEncoder.ready]);
  const io = new NodeIO()
    .registerExtensions(ALL_EXTENSIONS)
    .registerDependencies({ 'meshopt.decoder': MeshoptDecoder, 'meshopt.encoder': MeshoptEncoder });

  const document = await io.read(input);
  log(`read ${input}: ${document.getRoot().listNodes().length} nodes, ${document.getRoot().listTextures().length} textures.`);

  await document.transform(
    dedup(),
    weld(),
    prune({ keepLeaves: true, keepAttributes: false, keepSolidTextures: false, keepExtras: true }),
    ktx2(),
    meshoptWithoutPositionQuantization(MeshoptEncoder),
  );

  await io.write(output, document);
  log(`wrote ${output}.`);
}

main().catch((err) => {
  console.error(`optimize-scene: failed: ${err?.stack ?? err}`);
  process.exit(1);
});
