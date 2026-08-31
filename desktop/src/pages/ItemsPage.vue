<script setup lang="ts">
import { computed, ref } from 'vue'
import { Search, Trash2, PackageOpen, MapPin } from '../icons'
import { useAppStore } from '../stores/app'
import { useUiStore } from '../stores/ui'
import type { CatalogItem } from '../types'
import VirtualCatalogGrid from '../components/VirtualCatalogGrid.vue'

const store = useAppStore()
const ui = useUiStore()
const consumableSearch = ref('')
const catapultSearch = ref('')

function filtered(key: string, search: string) {
  const term = search.trim().toLocaleLowerCase()
  return store.catalogItems(key).filter((item) => !term || [item.name, item.enumName, item.id].join(' ').toLocaleLowerCase().includes(term))
}
const consumables = computed(() => filtered('disposables', consumableSearch.value))
const catapults = computed(() => filtered('catapultPoints', catapultSearch.value))
const consumableCounts = computed(() => ownedCounts(store.cheatState?.ownedConsumables, 'disposableId'))
const catapultCounts = computed(() => ownedCounts(store.cheatState?.ownedCatapultPoints, 'disposableId'))
const fieldDeleteMode = computed(() => store.cheatState?.fieldCatapultDeleteMode === true)

function ownedCounts(items: any[] | undefined, key: string) {
  const result: Record<string, number> = {}
  for (const item of items ?? []) result[item[key] || item.enumName || item.id] = Number(item.count || 0)
  return result
}
async function grantConsumable(_button: 'left' | 'right', item: CatalogItem) {
  if ((consumableCounts.value[item.id] ?? 0) >= 5) {
    ui.toast('该消耗品已经达到游戏持有上限 5。', 'warning')
    return
  }
  if (await store.command('cheat.grantDisposable', { disposableId: item.id, count: 1 })) await store.refreshState()
}
async function grantCatapult(_button: 'left' | 'right', item: CatalogItem) {
  if (await store.command('cheat.grantCatapultPoint', { disposableId: item.id, count: 1 })) await store.refreshState()
}
async function setFieldDeleteMode(enabled: boolean) {
  if (await store.command('cheat.setFieldCatapultDeleteMode', { enabled })) await store.refreshState()
}
</script>

<template>
  <div class="items-workspace">
    <section class="inventory-column mechanical-section">
      <header class="section-heading compact-heading">
        <div class="heading-with-icon"><PackageOpen :size="20" /><div><h2>消耗品</h2><p>左键直接获取，单种最多持有 5 个。</p></div></div>
        <button class="button danger compact" :disabled="store.writeLocked" @click="store.command('cheat.clearConsumables').then(() => store.refreshState())"><Trash2 :size="16" />全部删除</button>
      </header>
      <label class="search-box"><Search :size="17" /><input v-model="consumableSearch" aria-label="搜索消耗品" placeholder="搜索消耗品" /></label>
      <VirtualCatalogGrid :items="consumables" :counts="consumableCounts" :disabled="store.writeLocked" @invoke="grantConsumable" />
      <footer class="fixed-operation-note">{{ consumables.length }} 项 · 点击卡片立即写入背包</footer>
    </section>
    <section class="inventory-column mechanical-section">
      <header class="section-heading compact-heading">
        <div class="heading-with-icon"><MapPin :size="20" /><div><h2>弹射点</h2><p>包含普通、能量和当前游戏配置的特殊站点。</p></div></div>
      </header>
      <label class="search-box"><Search :size="17" /><input v-model="catapultSearch" aria-label="搜索弹射点" placeholder="搜索弹射点" /></label>
      <VirtualCatalogGrid :items="catapults" :counts="catapultCounts" :disabled="store.writeLocked" @invoke="grantCatapult" />
      <label class="field-delete-toggle" :class="{ disabled: store.writeLocked }">
        <input type="checkbox" :checked="fieldDeleteMode" :disabled="store.writeLocked" @change="setFieldDeleteMode(($event.target as HTMLInputElement).checked)" />
        <span class="switch-track"><span /></span>
        <span>点击场上弹射点直接删除</span>
      </label>
      <footer class="inventory-action-footer">
        <button class="button danger compact" :disabled="store.writeLocked" @click="store.command('cheat.clearBackpackCatapultPoints').then(() => store.refreshState())">清空背包</button>
        <button class="button danger compact" :disabled="store.writeLocked" @click="store.command('cheat.clearFieldCatapultPoints').then(() => store.refreshState())">清空场上</button>
      </footer>
    </section>
  </div>
</template>
