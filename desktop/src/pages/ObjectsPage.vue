<script setup lang="ts">
import { computed, ref } from 'vue'
import { RefreshCw, TrainFront, Bug, Save, Trash2 } from '../icons'
import { useAppStore } from '../stores/app'

const store = useAppStore()
const active = ref<'vehicles' | 'enemies'>('vehicles')
const selected = ref<Record<string, any>>()
const attributeId = ref('')
const attributeValue = ref(0)
const enchantmentId = ref('')
const enchantmentLevel = ref(1)
const rows = computed(() => active.value === 'vehicles' ? store.vehicles : store.enemies)
const attributes = computed(() => selected.value?.attributes ?? [])
const enchantments = computed(() => store.catalogItems('enchantments'))

async function refresh() {
  selected.value = undefined
  if (active.value === 'vehicles') await store.refreshVehicles()
  else await store.refreshEnemies()
}
function selectRow(row: Record<string, any>) {
  selected.value = row
  const first = row.attributes?.[0]
  attributeId.value = first?.id ?? ''
  attributeValue.value = Number(first?.baseValue ?? first?.value ?? 0)
  const firstEnchantment = row.enchantments?.[0]
  enchantmentId.value = firstEnchantment?.id ?? firstEnchantment?.enumName ?? enchantments.value[0]?.id ?? ''
  enchantmentLevel.value = Number(firstEnchantment?.level ?? 1)
}
function selectAttribute() {
  const attribute = attributes.value.find((item: any) => item.id === attributeId.value)
  attributeValue.value = Number(attribute?.baseValue ?? attribute?.value ?? 0)
}
async function modify() {
  if (!selected.value || !attributeId.value) return
  const command = active.value === 'vehicles' ? 'cheat.modifyVehicle' : 'cheat.modifyEnemy'
  const identity = active.value === 'vehicles'
    ? { vehicleId: selected.value.vehicleId || selected.value.runtimeId || selected.value.id }
    : { runtimeId: selected.value.runtimeId || selected.value.id }
  if (await store.command(command, { ...identity, attributeId: attributeId.value, value: attributeValue.value })) await refresh()
}
async function setEnchantment() {
  if (!selected.value || !enchantmentId.value || active.value !== 'vehicles') return
  if (await store.command('cheat.setVehicleEnchantment', {
    vehicleId: selected.value.vehicleId || selected.value.runtimeId || selected.value.id,
    enchantmentId: enchantmentId.value,
    level: Math.max(0, Math.trunc(enchantmentLevel.value)),
  })) await refresh()
}
</script>

<template>
  <div class="objects-workspace">
    <section class="card entity-list mechanical-section">
      <header class="section-heading compact-heading">
        <div class="tabs tabs-box segmented-control" role="tablist" aria-label="对象类型">
          <button class="tab" :class="{ active: active === 'vehicles' }" role="tab" :aria-selected="active === 'vehicles'" @click="active = 'vehicles'; refresh()"><TrainFront :size="16" />战车</button>
          <button class="tab" :class="{ active: active === 'enemies' }" role="tab" :aria-selected="active === 'enemies'" @click="active = 'enemies'; refresh()"><Bug :size="16" />怪物</button>
        </div>
        <button class="btn btn-square btn-ghost btn-sm icon-button" aria-label="刷新对象" @click="refresh"><RefreshCw :size="17" /></button>
      </header>
      <div class="entity-table" role="listbox">
        <button v-for="row in rows" :key="row.runtimeId || row.vehicleId || row.id" :class="{ active: selected === row }" @click="selectRow(row)">
          <span class="entity-icon"><TrainFront v-if="active === 'vehicles'" :size="18" /><Bug v-else :size="18" /></span>
          <span><strong>{{ row.name || row.enumName || row.id }}</strong><small>{{ row.enumName || row.runtimeId || row.vehicleId }}</small></span>
          <span>{{ row.health != null ? `${row.health} / ${row.maxHealth}` : row.level ? `L${row.level}` : '' }}</span>
        </button>
        <p v-if="rows.length === 0" class="empty-state">点击刷新读取当前场景对象。</p>
      </div>
    </section>
    <section class="card entity-editor mechanical-section" :class="{ mutationLocked: store.writeLocked }">
      <header class="section-heading"><div><h2>{{ selected?.name || '选择一个对象' }}</h2><p>{{ selected?.enumName || selected?.runtimeId || '选择后读取当前属性与附魔' }}</p></div></header>
      <template v-if="selected">
        <div class="entity-summary">
          <div><span>位置</span><strong>{{ selected.position ? `${selected.position.x} / ${selected.position.y} / ${selected.position.z}` : '-' }}</strong></div>
          <div><span>运行时 ID</span><strong>{{ selected.runtimeId || selected.vehicleId || '-' }}</strong></div>
        </div>
        <div class="form-grid two-columns">
          <label>属性<select v-model="attributeId" class="select select-bordered" name="attribute" autocomplete="off" @change="selectAttribute"><option v-for="attribute in attributes" :key="attribute.id" :value="attribute.id">{{ attribute.name || attribute.id }}</option></select></label>
          <label>目标值<input v-model.number="attributeValue" class="input input-bordered" name="attribute-value" autocomplete="off" type="number" inputmode="decimal" step="0.1" /></label>
        </div>
        <div class="action-bar right">
          <button class="btn btn-primary button primary" :disabled="store.writeLocked || !attributeId" @click="modify"><Save :size="17" />应用修改</button>
          <button v-if="active === 'vehicles'" class="btn btn-error button danger" :disabled="store.writeLocked" @click="store.command('cheat.removeVehicle', { vehicleId: selected.vehicleId || selected.runtimeId }).then(refresh)"><Trash2 :size="17" />删除战车</button>
        </div>
        <div v-if="active === 'vehicles'" class="enchantment-readout">
          <h3>当前附魔</h3>
          <div><span v-for="item in selected.enchantments || []" :key="item.id">{{ item.name || item.enumName || item.id }} · Lv.{{ item.level }}</span></div>
        </div>
        <div v-if="active === 'vehicles'" class="enchantment-editor">
          <label>附魔<select v-model="enchantmentId" class="select select-bordered" name="enchantment" autocomplete="off"><option v-for="item in enchantments" :key="item.id" :value="item.id">{{ item.name || item.enumName || item.id }} · {{ item.enumName || item.id }}</option></select></label>
          <label>层数（0 为删除）<input v-model.number="enchantmentLevel" class="input input-bordered" name="enchantment-level" autocomplete="off" type="number" inputmode="numeric" min="0" max="2147483647" /></label>
          <button class="btn btn-outline button secondary" :disabled="store.writeLocked || !enchantmentId" @click="setEnchantment"><Save :size="17" />设置附魔</button>
        </div>
      </template>
      <p v-else class="empty-editor">对象属性读取保持可用；自动游玩期间修改区会被锁定。</p>
    </section>
  </div>
</template>
