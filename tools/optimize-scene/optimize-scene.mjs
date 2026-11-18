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
//
// The viewer's "game look" needs baked lighting to survive (data contract: MOODYBLUES_scene):
//
//   - the root extension MOODYBLUES_scene is round-tripped, and its lightmap textures are held as
//     real graph references so prune keeps them and the KTX2 step sees them. gltf-transform
//     drops extensions it has no class for, so MoodyBluesScene below is mandatory.
//   - TEXCOORD_1 is kept on every mesh of a node with `extras.lightmap` (nothing in the material
//     reads it, so prune would delete it) and TEXCOORD is quantized at 16 bits.
//   - lightmap textures are encoded UASTC, linear, with alpha (RGBM): ETC1S or an sRGB tag would
//     corrupt the decode `rgb * a * range`.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  Extension,
  ExtensionProperty,
  ImageUtils,
  Material,
  NodeIO,
  Primitive,
  PropertyType,
  RefList,
  Texture,
  TextureChannel,
  TextureInfo,
} from '@gltf-transform/core';
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
const CLAMP_TO_EDGE = 33071; // glTF sampler wrap mode
// Data maps need the higher-quality UASTC; everything else is ETC1S. Same split as `optimize`.
const UASTC_SLOTS = /^(normalTexture|occlusionTexture|metallicRoughnessTexture)$/;
const { R: CHANNEL_R, G: CHANNEL_G, A: CHANNEL_A } = TextureChannel;

const MOODYBLUES_SCENE = 'MOODYBLUES_scene';
const SCENE_PROPERTY = 'MoodyBluesScene';
const LIGHTMAP_PROPERTY = 'MoodyBluesLightmap';

/**
 * One `lightmaps[i]` entry. `texture` is a real reference; every other member (`encoding`, `range`,
 * and whatever a later contract version adds) is kept as JSON text and written back verbatim. JSON
 * text rather than an object because property-graph treats plain-object attributes as ref maps.
 */
class MoodyBluesLightmap extends ExtensionProperty {
  static EXTENSION_NAME = MOODYBLUES_SCENE;

  init() {
    this.extensionName = MOODYBLUES_SCENE;
    this.propertyType = LIGHTMAP_PROPERTY;
    this.parentTypes = [SCENE_PROPERTY];
  }

  getDefaults() {
    return Object.assign(super.getDefaults(), { texture: null, membersJson: '{}' });
  }

  getTexture() {
    return this.getRef('texture');
  }

  setTexture(texture) {
    return this.setRef('texture', texture);
  }

  getMembers() {
    return JSON.parse(this.get('membersJson'));
  }

  setMembers(members) {
    return this.set('membersJson', JSON.stringify(members));
  }
}

/** The root `MOODYBLUES_scene` object: the lightmap entries as references, the rest verbatim. */
class MoodyBluesSceneSettings extends ExtensionProperty {
  static EXTENSION_NAME = MOODYBLUES_SCENE;

  init() {
    this.extensionName = MOODYBLUES_SCENE;
    this.propertyType = SCENE_PROPERTY;
    this.parentTypes = [PropertyType.ROOT];
  }

  getDefaults() {
    return Object.assign(super.getDefaults(), { lightmaps: new RefList(), membersJson: '{}' });
  }

  listLightmaps() {
    return this.listRefs('lightmaps');
  }

  addLightmap(lightmap) {
    return this.addRef('lightmaps', lightmap);
  }

  getMembers() {
    return JSON.parse(this.get('membersJson'));
  }

  setMembers(members) {
    return this.set('membersJson', JSON.stringify(members));
  }
}

/**
 * Round-trips the root `MOODYBLUES_scene` extension (listed in extensionsUsed, never required: a
 * viewer without it still shows the geometry). The glTF writer only emits root extension JSON
 * from `write()`, but KHR_texture_basisu rewrites `textures[]` in its own `write()`, which runs
 * first (extensions write in name order). The lightmaps' texture definitions are therefore
 * created in `prewrite(MATERIAL)`, which runs right after the images exist, so basisu sees them.
 */
export class MoodyBluesScene extends Extension {
  static EXTENSION_NAME = MOODYBLUES_SCENE;
  extensionName = MOODYBLUES_SCENE;
  prewriteTypes = [PropertyType.MATERIAL];
  #textureIndices = new Map();

  /** The settings property on the document's root, if the scene has one. */
  getSettings() {
    return this.document.getRoot().getExtension(MOODYBLUES_SCENE);
  }

  read(context) {
    const def = context.jsonDoc.json.extensions?.[MOODYBLUES_SCENE];
    if (!def) return this;

    const { lightmaps = [], ...members } = def;
    if (!Array.isArray(lightmaps)) throw new Error(`${MOODYBLUES_SCENE}: lightmaps must be an array.`);

    const textureDefs = context.jsonDoc.json.textures ?? [];
    const settings = new MoodyBluesSceneSettings(this.document.getGraph()).setMembers(members);
    lightmaps.forEach((lightmapDef, i) => {
      const { texture: textureIndex, ...rest } = lightmapDef;
      const source = textureDefs[textureIndex]?.source;
      const texture = source === undefined ? undefined : context.textures[source];
      if (!texture) throw new Error(`${MOODYBLUES_SCENE}: lightmaps[${i}].texture ${textureIndex} is not a texture.`);
      settings.addLightmap(new MoodyBluesLightmap(this.document.getGraph()).setTexture(texture).setMembers(rest));
    });
    this.document.getRoot().setExtension(MOODYBLUES_SCENE, settings);
    return this;
  }

  prewrite(context) {
    this.#textureIndices.clear();
    const settings = this.getSettings();
    if (!settings) return this;

    // Lightmaps are atlases: wrapping would bleed one chart's edge into the opposite side.
    const info = new TextureInfo(this.document.getGraph(), 'lightmapTextureInfo')
      .setWrapS(CLAMP_TO_EDGE)
      .setWrapT(CLAMP_TO_EDGE);
    for (const lightmap of settings.listLightmaps()) {
      const texture = lightmap.getTexture();
      if (!texture) throw new Error(`${MOODYBLUES_SCENE}: a lightmap lost its texture.`);
      this.#textureIndices.set(lightmap, context.createTextureInfoDef(texture, info).index);
    }
    info.dispose();
    return this;
  }

  write(context) {
    const settings = this.getSettings();
    if (!settings) return this;

    const lightmaps = settings.listLightmaps().map((lightmap) => ({
      ...lightmap.getMembers(),
      texture: this.#textureIndices.get(lightmap),
    }));
    const json = context.jsonDoc.json;
    json.extensions = { ...json.extensions, [MOODYBLUES_SCENE]: { ...settings.getMembers(), lightmaps } };
    return this;
  }
}

/** The textures `MOODYBLUES_scene.lightmaps` points at. */
function listLightmapTextures(document) {
  const settings = document.getRoot().getExtension(MOODYBLUES_SCENE);
  return new Set(settings ? settings.listLightmaps().map((lightmap) => lightmap.getTexture()) : []);
}

/**
 * `prune({keepAttributes: false})`'s unused-attribute removal (see the pinned version's
 * src/transforms/prune.ts: listRequiredSemantics, listUnusedSemantics), except that TEXCOORD_1 is
 * kept on every primitive of a mesh used by a node with `extras.lightmap`. Prune's follow-up that
 * renumbers TEXCOORD_n to close gaps is left out: it would rename the lightmap set, and a gap is
 * valid glTF. Runs before `prune`, which then deletes the accessors this orphans.
 */
function pruneUnusedAttributes() {
  return (document) => {
    const lightmapped = new Set();
    for (const node of document.getRoot().listNodes()) {
      const mesh = node.getMesh();
      if (mesh && node.getExtras().lightmap) lightmapped.add(mesh);
    }

    for (const mesh of document.getRoot().listMeshes()) {
      for (const prim of mesh.listPrimitives()) {
        const material = prim.getMaterial();
        if (!material) continue;

        const required = listRequiredSemantics(document, prim, material);
        if (lightmapped.has(mesh)) required.add('TEXCOORD_1');
        const unused = prim.listSemantics().filter((semantic) => isUnusedSemantic(semantic, required));
        for (const semantic of unused) prim.setAttribute(semantic, null);
        for (const target of prim.listTargets()) {
          for (const semantic of unused) target.setAttribute(semantic, null);
        }
      }
    }
  };
}

function isUnusedSemantic(semantic, required) {
  if (semantic === 'NORMAL' || semantic === 'TANGENT' || semantic.startsWith('TEXCOORD_')) {
    return !required.has(semantic);
  }
  return semantic.startsWith('COLOR_') && semantic !== 'COLOR_0';
}

/** Attributes a material needs (not the unconditional POSITION): texcoord sets, TANGENT, NORMAL. */
function listRequiredSemantics(document, prim, material, semantics = new Set()) {
  const edges = document.getGraph().listChildEdges(material);
  const textureNames = new Set();
  for (const edge of edges) {
    if (edge.getChild() instanceof Texture) textureNames.add(edge.getName());
  }

  for (const edge of edges) {
    const name = edge.getName();
    const child = edge.getChild();
    if (child instanceof TextureInfo && textureNames.has(name.replace(/Info$/, ''))) {
      semantics.add(`TEXCOORD_${child.getTexCoord()}`);
    }
    if (child instanceof Texture && /normalTexture/i.test(name)) semantics.add('TANGENT');
    if (child instanceof ExtensionProperty) listRequiredSemantics(document, prim, child, semantics);
  }

  const isLit = material instanceof Material && !material.getExtension('KHR_materials_unlit');
  if (isLit && prim.getMode() !== Primitive.Mode.POINTS) semantics.add('NORMAL');
  return semantics;
}

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

function threadParams(numTextures) {
  const cpus = os.cpus().length || 1;
  return numTextures > 1 ? ['--threads', Math.max(2, Math.min(cpus, Math.round((3 * cpus) / numTextures)))] : [];
}

/**
 * Lightmaps are RGBM in linear space and have no material slot, so ktxParams' slot-derived color
 * space and channels do not apply. UASTC without RDO: RDO's smoothing would put visible steps in
 * the multiplier in alpha, which is amplified by `range`.
 */
function lightmapKtxParams(numTextures) {
  return [
    '--generate-mipmap',
    '--encode', 'uastc', '--uastc-quality', 4, '--zstd', 18,
    '--assign-tf', 'linear', '--assign-primaries', 'none',
    '--format', 'R8G8B8A8_UNORM',
    ...threadParams(numTextures),
  ];
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

  return [...params, ...threadParams(numTextures)];
}

/** Resizes (to at most MAX_TEXTURE_SIZE, dimensions rounded up to a multiple of four) and encodes every PNG/JPEG as KTX2. */
function ktx2() {
  return async (document) => {
    const textures = document.getRoot().listTextures();
    const work = textures.filter((t) => ['image/png', 'image/jpeg'].includes(t.getMimeType()));
    if (work.length === 0) return;

    const basisu = document.createExtension(KHRTextureBasisu).setRequired(true);
    const lightmaps = listLightmapTextures(document);
    const dir = await mkdtemp(path.join(os.tmpdir(), 'moodyblues-ktx-'));
    try {
      const jobs = Math.max(1, Math.min(os.cpus().length || 1, 4));
      let next = 0;
      const worker = async () => {
        while (next < work.length) {
          const index = next++;
          await encodeTexture(work[index], index, work.length, dir, lightmaps.has(work[index]));
        }
      };
      await Promise.all(Array.from({ length: jobs }, worker));
    } finally {
      await rm(dir, { recursive: true, force: true });
    }

    if (!textures.some((t) => t.getMimeType() === 'image/ktx2')) basisu.dispose();
  };
}

async function encodeTexture(texture, index, total, dir, isLightmap) {
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
  const params = isLightmap ? lightmapKtxParams(total) : ktxParams(texture, slots, isUastc, total);
  const args = ['create', ...params, src, dst];
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
        // Lightmap UVs index an atlas; the default 12 bits is too coarse for it.
        quantizeTexcoord: 16,
      }),
    );
    document
      .createExtension(EXTMeshoptCompression)
      .setRequired(true)
      .setEncoderOptions({ method: EXTMeshoptCompression.EncoderMethod.FILTER });
  };
}

/** The optimization pipeline (see the header); `encodeTextures: false` skips the `ktx` step. */
export async function optimizeDocument(document, { encoder = MeshoptEncoder, encodeTextures = true } = {}) {
  await document.transform(
    dedup(),
    weld(),
    pruneUnusedAttributes(),
    prune({ keepLeaves: true, keepAttributes: true, keepSolidTextures: false, keepExtras: true }),
    ...(encodeTextures ? [ktx2()] : []),
    meshoptWithoutPositionQuantization(encoder),
  );
}

export async function createOptimizerIO() {
  await Promise.all([MeshoptDecoder.ready, MeshoptEncoder.ready]);
  return new NodeIO()
    .registerExtensions([...ALL_EXTENSIONS, MoodyBluesScene])
    .registerDependencies({ 'meshopt.decoder': MeshoptDecoder, 'meshopt.encoder': MeshoptEncoder });
}

async function main() {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) {
    throw new Error('usage: node optimize-scene.mjs <input.glb> <output.glb>');
  }

  const io = await createOptimizerIO();
  const document = await io.read(input);
  log(`read ${input}: ${document.getRoot().listNodes().length} nodes, ${document.getRoot().listTextures().length} textures.`);

  await optimizeDocument(document);

  await io.write(output, document);
  log(`wrote ${output}.`);
}

// Only when run as a script, so the tests can import the pipeline.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((err) => {
    console.error(`optimize-scene: failed: ${err?.stack ?? err}`);
    process.exit(1);
  });
}
