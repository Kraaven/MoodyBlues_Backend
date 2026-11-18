// Run with `npm test`. Builds a small scene GLB in memory, runs the optimizer pipeline on it and
// checks what the viewer's game look depends on. The KTX2 assertions need the `ktx` CLI.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';
import { Document } from '@gltf-transform/core';
import { KHR_DF_MODEL_UASTC, KHR_DF_TRANSFER_LINEAR, read } from 'ktx-parse';
import sharp from 'sharp';
import { MoodyBluesScene, createOptimizerIO, optimizeDocument } from './optimize-scene.mjs';

// KTX2 spec, UASTC data format descriptor: channel 0 is RGB, channel 3 is RGBA.
const KHR_DF_CHANNEL_UASTC_RGBA = 3;
const hasKtx = spawnSync('ktx', ['--version'], { stdio: 'ignore' }).status === 0;

const SCENE_EXTENSION = {
  version: 1,
  ambient: { cube: [[1, 0, 0], [0, 1, 0], [0, 0, 1], [1, 1, 0], [0, 1, 1], [1, 0, 1]] },
  fog: null,
  sun: 7,
  lighting: { mixedMode: 'subtractive' },
  futureMember: { keep: ['me', 1] },
  lightmaps: [{ texture: 0, encoding: 'rgbm', range: 8, extraEntryMember: true }],
};

// 16x16 RGBA gradient with a varying alpha, so it is neither solid nor opaque.
async function lightmapPng() {
  const size = 16;
  const pixels = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      pixels.set([x * 16, y * 16, 128, 64 + x * 8], (y * size + x) * 4);
    }
  }
  return sharp(pixels, { raw: { width: size, height: size, channels: 4 } }).png().toBuffer();
}

/** Writes the test scene with the same IO the optimizer uses and returns its bytes. */
async function buildGlb(io) {
  const document = new Document();
  document.createBuffer();
  document.createExtension(MoodyBluesScene);

  const quad = (semantic, values, itemSize) =>
    document.createAccessor().setType(itemSize === 3 ? 'VEC3' : 'VEC2').setArray(new Float32Array(values));
  const position = quad('POSITION', [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], 3);
  const normal = quad('NORMAL', [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1], 3);
  const uv0 = quad('TEXCOORD_0', [0, 0, 1, 0, 1, 1, 0, 1], 2);
  const uv1 = quad('TEXCOORD_1', [0, 0, 0.5, 0, 0.5, 0.5, 0, 0.5], 2);
  const indices = document.createAccessor().setType('SCALAR').setArray(new Uint16Array([0, 1, 2, 0, 2, 3]));

  const material = document.createMaterial('plain');
  const lit = document.createPrimitive().setIndices(indices).setMaterial(material)
    .setAttribute('POSITION', position).setAttribute('NORMAL', normal)
    .setAttribute('TEXCOORD_0', uv0).setAttribute('TEXCOORD_1', uv1);
  const lightmappedMesh = document.createMesh('lightmapped').addPrimitive(lit);
  // A second mesh (different positions, or dedup would merge it into the first) without a
  // lightmapped node: its unused UVs must go.
  const otherPosition = quad('POSITION', [0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1], 3);
  const plain = document.createPrimitive().setIndices(indices).setMaterial(material)
    .setAttribute('POSITION', otherPosition).setAttribute('NORMAL', normal)
    .setAttribute('TEXCOORD_0', uv0).setAttribute('TEXCOORD_1', uv1);
  const plainMesh = document.createMesh('plain').addPrimitive(plain);

  const lightmappedNode = document.createNode('a').setMesh(lightmappedMesh)
    .setExtras({ objectId: 11, lightmap: { index: 0, scaleOffset: [0.5, 0.5, 0, 0.5] } });
  const sibling = document.createNode('b').setMesh(lightmappedMesh).setExtras({ objectId: 12 });
  const other = document.createNode('c').setMesh(plainMesh).setExtras({ objectId: 13 });
  document.createScene().addChild(lightmappedNode).addChild(sibling).addChild(other);

  const texture = document.createTexture('lightmap0').setMimeType('image/png').setImage(await lightmapPng());
  const { lightmaps, ...members } = SCENE_EXTENSION;
  // Built through the JSON path so the test covers the reader, not only the in-memory classes.
  const json = await io.writeJSON(document);
  json.json.extensionsUsed.push('MOODYBLUES_scene');
  json.json.textures = [{ source: 0 }];
  json.json.extensions = { MOODYBLUES_scene: { ...members, lightmaps } };
  return { json, texture };
}

async function optimizedScene({ encodeTextures }) {
  const io = await createOptimizerIO();
  const { json } = await buildGlb(io);
  const document = await io.readJSON(json);
  await optimizeDocument(document, { encodeTextures });
  const out = await io.writeJSON(document);
  return { document, out, io };
}

test('MOODYBLUES_scene survives with every member, never required', async () => {
  const { out } = await optimizedScene({ encodeTextures: false });
  const ext = out.json.extensions.MOODYBLUES_scene;
  const { lightmaps, ...members } = SCENE_EXTENSION;
  for (const [key, value] of Object.entries(members)) assert.deepEqual(ext[key], value, key);
  assert.equal(ext.lightmaps.length, 1);
  assert.deepEqual({ ...ext.lightmaps[0], texture: 0 }, lightmaps[0]);
  assert.ok(out.json.extensionsUsed.includes('MOODYBLUES_scene'));
  assert.ok(!(out.json.extensionsRequired ?? []).includes('MOODYBLUES_scene'));

  const textureDef = out.json.textures[ext.lightmaps[0].texture];
  assert.ok(textureDef, 'lightmap texture index resolves to a texture');
  assert.equal(out.json.images.length, 1, 'lightmap image kept by prune');
});

test('TEXCOORD_1 kept on lightmapped meshes only; objectId extras intact', async () => {
  const { document } = await optimizedScene({ encodeTextures: false });
  const semantics = (name) =>
    document.getRoot().listMeshes().find((m) => m.getName() === name).listPrimitives()[0].listSemantics();
  assert.ok(semantics('lightmapped').includes('TEXCOORD_1'), 'lightmapped keeps TEXCOORD_1');
  assert.ok(!semantics('lightmapped').includes('TEXCOORD_0'), 'unused TEXCOORD_0 removed');
  assert.ok(!semantics('plain').some((s) => s.startsWith('TEXCOORD_')), 'unused UVs removed elsewhere');

  const nodes = document.getRoot().listNodes();
  assert.deepEqual(nodes.map((n) => n.getExtras().objectId), [11, 12, 13]);
  assert.deepEqual(nodes[0].getExtras().lightmap, { index: 0, scaleOffset: [0.5, 0.5, 0, 0.5] });
  assert.equal(nodes[0].getMesh(), nodes[1].getMesh(), 'meshes are not merged or instanced away');
});

test('TEXCOORD is quantized at 16 bits', async () => {
  const { document } = await optimizedScene({ encodeTextures: false });
  const mesh = document.getRoot().listMeshes().find((m) => m.getName() === 'lightmapped');
  const uv1 = mesh.listPrimitives()[0].getAttribute('TEXCOORD_1');
  assert.equal(uv1.getComponentType(), 5123, 'UNSIGNED_SHORT');
  assert.ok(uv1.getNormalized());
});

test('lightmap texture is KTX2 UASTC, linear, with alpha', { skip: !hasKtx && 'ktx CLI not on PATH; KTX2 encoding not tested' }, async () => {
  const { document, out } = await optimizedScene({ encodeTextures: true });
  const texture = document.getRoot().listTextures()[0];
  assert.equal(texture.getMimeType(), 'image/ktx2');

  const container = read(texture.getImage());
  const dfd = container.dataFormatDescriptor[0];
  assert.equal(dfd.colorModel, KHR_DF_MODEL_UASTC);
  assert.equal(dfd.transferFunction, KHR_DF_TRANSFER_LINEAR);
  assert.ok(dfd.samples.some((s) => s.channelType === KHR_DF_CHANNEL_UASTC_RGBA), 'alpha kept');
  assert.ok(container.levels.length > 1, 'mipmaps');

  const ext = out.json.extensions.MOODYBLUES_scene;
  assert.ok(out.json.textures[ext.lightmaps[0].texture].extensions.KHR_texture_basisu, 'texture def uses basisu');
});
