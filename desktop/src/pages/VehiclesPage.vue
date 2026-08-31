<script setup lang="ts">
import { computed, ref } from 'vue'
import { Search, Plus, Trash2, TrainFront } from '../icons'
import { useAppStore } from '../stores/app'
import type { CatalogItem } from '../types'
import CatalogCard from '../components/CatalogCard.vue'
import VirtualCatalogGrid from '../components/VirtualCatalogGrid.vue'

const store = useAppStore()
const search = ref('')
const enchantmentSearch = ref('')
const activeType = ref('all')
const selectedVehicle = ref<CatalogItem>()
const count = ref(1)
const enchantments = ref<Record<string, number>>({})

const vehicles = computed(() => store.catalogItems('vehicles'))
const types = computed(() => {
  const found = new Map<string, { key: string; label: string; order: number }>()
  for (const item of vehicles.value) {
    const key = item.typeKey || String(item.enumName || item.id).split('_')[0]
    if (!found.has(key)) found.set(key, { key, label: item.typeName || translateType(key), order: item.typeOrder ?? 999 })
  }
  return [...found.values()].sort((a, b) => a.order - b.order || a.label.localeCompare(b.label, 'zh-CN'))
})
const filteredVehicles = computed(() => {
  const term = search.value.trim().toLocaleLowerCase()
  return vehicles.value.filter((item) => {
    const type = item.typeKey || String(item.enumName || item.id).split('_')[0]
    const matchesType = term ? true : activeType.value === 'all' || activeType.value === type
    const haystack = [item.name, item.enumName, item.id, item.typeKey, item.familyKey].join(' ').toLocaleLowerCase()
    return matchesType && (!term || haystack.includes(term))
  })
})
const families = computed(() => {
  const groups = new Map<string, CatalogItem[]>()
  for (const item of filteredVehicles.value) {
    const key = item.familyKey || String(item.enumName || item.id).replace(/_L\d+$/i, '')
    if (!groups.has(key)) groups.set(key, [])
    groups.get(key)!.push(item)
  }
  return [...groups.entries()].map(([key, items]) => ({
    key,
    items: items.sort((a, b) => (a.level ?? 999) - (b.level ?? 999) || (a.itemOrder ?? 999) - (b.itemOrder ?? 999)),
  }))
})
const enchantmentItems = computed(() => {
  const term = enchantmentSearch.value.trim().toLocaleLowerCase()
  return store.catalogItems('enchantments').filter((item) => !term || [item.name, item.enumName, item.id].join(' ').toLocaleLowerCase().includes(term))
})
const selectedEnchantments = computed(() => store.catalogItems('enchantments').filter((item) => (enchantments.value[item.id] ?? 0) > 0))

function translateType(type: string) {
  return ({ Shell: '炮弹', Link: '链接', Missile: '导弹', Penetrate: '穿透' } as Record<string, string>)[type] || type
}
function adjustEnchantment(button: 'left' | 'right', item: CatalogItem) {
  const current = enchantments.value[item.id] ?? 0
  const next = button === 'left' ? Math.min(2147483647, current + 1) : Math.max(0, current - 1)
  enchantments.value = { ...enchantments.value, [item.id]: next }
}
async function grantVehicle() {
  if (!selectedVehicle.value) return
  await store.command('cheat.grantVehicle', {
    vehicleId: selectedVehicle.value.id,
    count: count.value,
    enchantments: Object.entries(enchantments.value).filter(([, level]) => level > 0).map(([enchantmentId, level]) => ({ enchantmentId, level })),
  })
}
</script>

<template>
  <div class="vehicle-workspace">
    <section class="vehicle-selector mechanical-section">
      <div class="search-row">
        <label class="search-box"><Search :size="17" /><input v-model="search" aria-label="搜索战车" placeholder="搜索中文名、枚举或系列" /></label>
      </div>
      <div class="type-switcher">
        <button :class="{ active: activeType === 'all' }" @click="activeType = 'all'">全部</button>
        <button v-for="type in types" :key="type.key" :class="{ active: activeType === type.key }" @click="activeType = type.key">{{ type.label }}</button>
      </div>
      <div class="vehicle-family-grid">
        <article v-for="family in families" :key="family.key" class="vehicle-family" :class="{ selected: family.items.some(item => item.id === selectedVehicle?.id) }">
          <div class="family-identity">
            <TrainFront :size="24" />
            <div><strong>{{ family.items[0].name || family.items[0].fallbackName || family.key }}</strong><small>{{ family.key }}</small></div>
          </div>
          <div class="level-buttons">
            <button
              v-for="item in family.items"
              :key="item.id"
              :class="{ active: selectedVehicle?.id === item.id }"
              @click="selectedVehicle = item"
            >L{{ item.level ?? '?' }}</button>
          </div>
        </article>
      </div>
    </section>

    <section class="vehicle-loadout mechanical-section" :class="{ mutationLocked: store.writeLocked }">
      <header class="section-heading"><div><h2>本次装配</h2><p>{{ selectedVehicle?.name || selectedVehicle?.enumName || '尚未选择战车' }}</p></div></header>
      <div class="selected-enchantment-wrap">
        <CatalogCard
          v-for="item in selectedEnchantments"
          :key="item.id"
          :item="item"
          :count="enchantments[item.id]"
          @invoke="adjustEnchantment"
        />
        <p v-if="selectedEnchantments.length === 0" class="empty-state">未选择附魔时获取普通战车。</p>
      </div>
      <div class="grant-row">
        <label>数量<input v-model.number="count" type="number" min="1" max="999" /></label>
        <button class="button primary" :disabled="!selectedVehicle || store.writeLocked" @click="grantVehicle"><Plus :size="17" />获取战车</button>
      </div>
    </section>

    <section class="enchantment-panel mechanical-section">
      <div class="search-row">
        <label class="search-box"><Search :size="17" /><input v-model="enchantmentSearch" aria-label="搜索附魔" placeholder="搜索附魔中文名或枚举" /></label>
        <button class="button secondary compact" @click="enchantments = {}"><Trash2 :size="16" />清空所选</button>
      </div>
      <VirtualCatalogGrid :items="enchantmentItems" :counts="enchantments" @invoke="adjustEnchantment" />
    </section>
  </div>
</template>
