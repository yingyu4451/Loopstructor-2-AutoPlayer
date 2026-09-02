<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Search, Gem, Download, Trash2 } from '../icons'
import { useAppStore } from '../stores/app'
import type { CatalogItem } from '../types'
import VirtualCatalogGrid from '../components/VirtualCatalogGrid.vue'

const store = useAppStore()
const search = ref('')
const items = computed(() => {
  const term = search.value.trim().toLocaleLowerCase()
  return store.catalogItems('relics').filter((item) => !term || [item.name, item.enumName, item.id].join(' ').toLocaleLowerCase().includes(term))
})
const ownedIds = computed(() => (store.cheatState?.ownedRelics ?? []).filter((item: any) => Number(item.count || 0) > 0).map((item: any) => item.relicId || item.enumName || item.id))
async function toggle(button: 'left' | 'right', item: CatalogItem) {
  const command = button === 'right' ? 'cheat.removeRelic' : 'cheat.grantRelic'
  if (await store.command(command, { relicId: item.id })) await store.refreshState()
}
const task = computed(() => store.cheatState?.grantAllRelics)
const removal = computed(() => store.cheatState?.removeAllRelics)
let pollTimer: number | undefined

async function runBatch(command: 'cheat.grantAllRelics' | 'cheat.removeAllRelics') {
  if (await store.command(command)) await store.refreshState(true)
}

onMounted(() => {
  pollTimer = window.setInterval(() => {
    if (task.value?.state === 'running' || removal.value?.state === 'running') void store.refreshState(true)
  }, 750)
})
onBeforeUnmount(() => { if (pollTimer) window.clearInterval(pollTimer) })
</script>

<template>
  <div class="relic-workspace mechanical-section">
    <header class="section-heading">
      <div class="heading-with-icon"><Gem :size="21" /><div><h2>遗物目录</h2><p>左键启用，右键移除；悬停 1 秒读取游戏原始说明。</p></div></div>
      <div class="header-actions">
        <button class="button secondary compact" :disabled="store.writeLocked" @click="runBatch('cheat.grantAllRelics')"><Download :size="16" />全部获取</button>
        <button class="button danger compact" :disabled="store.writeLocked" @click="runBatch('cheat.removeAllRelics')"><Trash2 :size="16" />全部删除</button>
      </div>
    </header>
    <label class="search-box"><Search :size="17" /><input v-model="search" name="relic-search" autocomplete="off" aria-label="搜索遗物" placeholder="搜索遗物中文名或枚举…" /></label>
    <VirtualCatalogGrid :items="items" :selected-ids="ownedIds" :disabled="store.writeLocked" @invoke="toggle" />
    <footer class="relic-footer">
      <span>{{ task?.state === 'running' ? task.message : removal?.state === 'running' ? removal.message : `已启用 ${ownedIds.length} / ${items.length}` }}</span>
      <progress v-if="task?.state === 'running'" :value="task.processedCount" :max="task.totalCount" />
      <progress v-else-if="removal?.state === 'running'" :value="removal.processedCount" :max="removal.totalCount" />
    </footer>
  </div>
</template>
