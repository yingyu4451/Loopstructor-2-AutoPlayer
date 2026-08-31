<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Search, Crosshair, Plus, Trash2, BugPlay } from '../icons'
import { useAppStore } from '../stores/app'
import type { CatalogItem } from '../types'
import VirtualCatalogGrid from '../components/VirtualCatalogGrid.vue'

const store = useAppStore()
const createId = () => crypto.randomUUID()
const search = ref('')
const selected = ref<CatalogItem>()
const level = ref(1)
const count = ref(1)
const radius = ref(0)
const followLevel = ref(true)
type SpawnPoint = { id: string; pointId: string; x: number; y: number; z: number }
const points = ref<SpawnPoint[]>([{ id: createId(), pointId: '', x: 0, y: 0, z: 0 }])
const captureState = computed(() => store.cheatState?.spawnPointCapture ?? {})
const enemies = computed(() => {
  const term = search.value.trim().toLocaleLowerCase()
  return store.catalogItems('enemies').filter((item) => !term || [item.name, item.enumName, item.id].join(' ').toLocaleLowerCase().includes(term))
})
async function capture() {
  const armed = captureState.value.state === 'armed'
  if (await store.command('cheat.setSpawnPointCapture', { enabled: !armed })) await store.refreshState(true)
}
function syncCapturedPoints(capture: Record<string, any>) {
  const incoming = Array.isArray(capture.points) ? capture.points : []
  const manual = points.value.filter((point) => !point.pointId)
  const captured = incoming.map((point: any): SpawnPoint => ({
    id: `captured-${point.pointId || point.id}`,
    pointId: String(point.pointId || point.id || ''),
    x: Number(point.x || 0),
    y: Number(point.y || 0),
    z: Number(point.z || 0),
  })).filter((point: SpawnPoint) => point.pointId)
  points.value = [...manual, ...captured]
}
async function removePoint(point: SpawnPoint) {
  if (point.pointId) {
    if (!await store.command('cheat.removeSpawnPoint', { pointId: point.pointId })) return
    await store.refreshState(true)
  } else points.value = points.value.filter((item) => item.id !== point.id)
}
async function clearPoints() {
  if (points.value.some((point) => point.pointId) && !await store.command('cheat.clearSpawnPoints')) return
  points.value = []
  await store.refreshState(true)
}
async function spawn() {
  if (!selected.value) return
  const pointIds = points.value.filter((point) => point.pointId).map((point) => point.pointId)
  await store.command('cheat.spawnEnemy', {
    enemyId: selected.value.id,
    enumName: selected.value.enumName,
    levelMode: followLevel.value ? 'current' : 'custom',
    useCurrentLevel: followLevel.value,
    ...(followLevel.value ? {} : { level: level.value }),
    count: count.value,
    spawnRadius: radius.value,
    pointIds,
    points: points.value.map(({ pointId, x, y, z }) => ({ pointId: pointId || undefined, x, y, z })),
  })
}
let capturePoll: number | undefined
watch(captureState, (capture) => syncCapturedPoints(capture), { deep: true, immediate: true })
onMounted(() => {
  capturePoll = window.setInterval(() => {
    if (captureState.value.state === 'armed') void store.refreshState(true)
  }, 750)
})
onBeforeUnmount(() => {
  if (capturePoll) window.clearInterval(capturePoll)
  if (captureState.value.state === 'armed') void store.command('cheat.setSpawnPointCapture', { enabled: false }, false)
})
</script>

<template>
  <div class="spawn-workspace" :class="{ mutationLocked: store.writeLocked }">
    <section class="spawn-catalog mechanical-section">
      <label class="search-box"><Search :size="17" /><input v-model="search" aria-label="搜索怪物" placeholder="搜索怪物中文名或枚举" /></label>
      <VirtualCatalogGrid :items="enemies" :selected-ids="selected ? [selected.id] : []" :disabled="store.writeLocked" @invoke="(_button, item) => selected = item" />
    </section>
    <section class="spawn-editor mechanical-section">
      <header class="section-heading"><div><h2>生成怪物</h2><p>{{ selected?.name || selected?.enumName || '先从左侧选择怪物' }}</p></div></header>
      <div class="form-grid two-columns">
        <label class="switch-line">跟随当前关卡<input v-model="followLevel" type="checkbox" /></label>
        <label>自定义等级<input v-model.number="level" type="number" min="1" max="200" :disabled="followLevel" /></label>
        <label>数量<input v-model.number="count" type="number" min="1" max="100" /></label>
        <label>散布半径<input v-model.number="radius" type="number" min="0" step="0.5" /></label>
      </div>
      <div class="spawn-point-heading"><div><h3>生成位置</h3><small>{{ captureState.message || '可手工输入，或在游戏内按住左 Alt 后点击选点。' }}</small></div><div class="action-bar"><button class="button secondary compact" @click="capture"><Crosshair :size="16" />{{ captureState.state === 'armed' ? '取消选点' : '游戏内选点' }}</button><button class="icon-button danger-icon" aria-label="清空生成点" :disabled="points.length === 0" @click="clearPoints"><Trash2 :size="16" /></button></div></div>
      <div class="spawn-point-list">
        <div v-for="point in points" :key="point.id" class="coordinate-row">
          <input v-model.number="point.x" type="number" step="0.1" aria-label="X 坐标" />
          <input v-model.number="point.y" type="number" step="0.1" aria-label="Y 坐标" />
          <input v-model.number="point.z" type="number" step="0.1" aria-label="Z 坐标" />
          <button class="icon-button danger-icon" aria-label="删除生成点" @click="removePoint(point)"><Trash2 :size="16" /></button>
        </div>
      </div>
      <div class="action-bar between">
        <button class="button secondary compact" @click="points.push({ id: createId(), pointId: '', x: 0, y: 0, z: 0 })"><Plus :size="16" />添加位置</button>
        <button class="button primary" :disabled="!selected || points.length === 0 || store.writeLocked" @click="spawn"><BugPlay :size="18" />生成怪物</button>
      </div>
    </section>
  </div>
</template>
