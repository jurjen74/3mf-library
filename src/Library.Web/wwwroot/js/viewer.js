// 3D viewer for the file page. Loaded on demand by site.js; reads the server's compact mesh format
// (see Meshes/MeshCodec.cs) and shows it with three.js, Z up, rotate/zoom/pan with the mouse or touch.
import * as THREE from "../lib/three/three.module.js";
import { OrbitControls } from "../lib/three/OrbitControls.js";

const AZIMUTH = (35 * Math.PI) / 180;
const ELEVATION = (28 * Math.PI) / 180;

function parseMesh(buffer) {
  const view = new DataView(buffer);
  const magic = String.fromCharCode(view.getUint8(0), view.getUint8(1), view.getUint8(2), view.getUint8(3));
  if (magic !== "M3M1") throw new Error("Unknown mesh format");

  const vertexCount = view.getUint32(4, true);
  const triangleCount = view.getUint32(8, true);
  const groupCount = view.getUint32(12, true);
  const min = [view.getFloat32(16, true), view.getFloat32(20, true), view.getFloat32(24, true)];
  const max = [view.getFloat32(28, true), view.getFloat32(32, true), view.getFloat32(36, true)];

  const groups = [];
  let offset = 40;
  for (let g = 0; g < groupCount; g++, offset += 12) {
    groups.push({
      start: view.getUint32(offset, true),
      count: view.getUint32(offset + 4, true),
      colour: view.getUint32(offset + 8, true),
    });
  }

  const positions = new Uint16Array(buffer, offset, vertexCount * 3);
  offset += vertexCount * 6 + (vertexCount % 2 ? 2 : 0);
  const indices = new Uint32Array(buffer, offset, triangleCount * 3);
  return { min, max, groups, positions, indices, triangleCount };
}

export class Viewer {
  constructor(container) {
    this.container = container;
    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    container.append(this.renderer.domElement);

    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(30, 1, 0.1, 100000);
    this.camera.up.set(0, 0, 1);
    this.scene.add(this.camera);

    const sky = new THREE.HemisphereLight(0xffffff, 0x6f6a62, 1.5);
    sky.position.set(0, 0, 1);
    this.scene.add(sky);
    const key = new THREE.DirectionalLight(0xffffff, 1.8);
    key.position.set(-0.6, 1.2, 1);
    this.camera.add(key); // a headlight: lighting follows the view

    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.addEventListener("change", () => this.render());

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(container);
    this.resize();
  }

  async load(url, signal) {
    const response = await fetch(url, { signal });
    if (!response.ok) {
      let message = `Could not load the model (${response.status})`;
      try {
        const problem = await response.json();
        if (problem?.detail) message = problem.detail;
      } catch {
        // not JSON
      }
      throw new Error(message);
    }

    const data = parseMesh(await response.arrayBuffer());
    this.clear();
    if (data.triangleCount === 0) {
      this.render();
      return { triangles: 0, size: [0, 0, 0] };
    }

    const size = [0, 1, 2].map((i) => data.max[i] - data.min[i]);
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(data.positions, 3, true));
    geometry.setIndex(new THREE.BufferAttribute(data.indices, 1));
    const materials = data.groups.map((group, index) => {
      geometry.addGroup(group.start * 3, group.count * 3, index);
      return new THREE.MeshStandardMaterial({
        color: new THREE.Color(group.colour),
        flatShading: true,
        roughness: 0.62,
        metalness: 0.04,
        side: THREE.DoubleSide,
      });
    });

    // Positions arrive as 0..1 inside the bounding box: scale back to millimetres, centre on the bed.
    const mesh = new THREE.Mesh(geometry, materials);
    mesh.scale.set(size[0] || 1, size[1] || 1, size[2] || 1);
    mesh.position.set(-size[0] / 2, -size[1] / 2, 0);
    this.model = new THREE.Group();
    this.model.add(mesh);
    this.scene.add(this.model);

    const footprint = Math.max(size[0], size[1], 10) * 1.6;
    this.grid = new THREE.GridHelper(footprint, 16, 0x888888, 0x888888);
    this.grid.rotateX(Math.PI / 2);
    this.grid.material.transparent = true;
    this.grid.material.opacity = 0.25;
    this.scene.add(this.grid);

    this.frame(size);
    return { triangles: data.triangleCount, size };
  }

  frame(size) {
    const radius = Math.max(Math.hypot(size[0], size[1], size[2]) / 2, 1);
    const distance = (radius / Math.sin(((this.camera.fov / 2) * Math.PI) / 180)) * 1.08;
    const target = new THREE.Vector3(0, 0, size[2] / 2);
    this.camera.position.set(
      target.x + distance * Math.sin(AZIMUTH) * Math.cos(ELEVATION),
      target.y - distance * Math.cos(AZIMUTH) * Math.cos(ELEVATION),
      target.z + distance * Math.sin(ELEVATION),
    );
    this.camera.near = distance / 100;
    this.camera.far = distance * 100;
    this.camera.updateProjectionMatrix();
    this.controls.target.copy(target);
    this.controls.update();
    this.render();
  }

  clear() {
    for (const object of [this.model, this.grid]) {
      if (!object) continue;
      this.scene.remove(object);
      object.traverse((child) => {
        child.geometry?.dispose();
        const material = child.material;
        (Array.isArray(material) ? material : material ? [material] : []).forEach((m) => m.dispose());
      });
    }
    this.model = this.grid = null;
  }

  resize() {
    const width = this.container.clientWidth;
    const height = this.container.clientHeight;
    if (!width || !height) return;
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.render();
  }

  render() {
    this.renderer.render(this.scene, this.camera);
  }

  dispose() {
    this.resizeObserver.disconnect();
    this.controls.dispose();
    this.clear();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
    this.renderer.domElement.remove();
  }
}
