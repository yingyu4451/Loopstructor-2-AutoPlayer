<script setup lang="ts">
import { ImageOff, Check } from '../icons'
import { computed } from 'vue'
import type { CatalogItem } from '../types'

const props = defineProps<{
  item: CatalogItem
  count?: number
  selected?: boolean
  disabled?: boolean
}>()
const emit = defineEmits<{ invoke: [button: 'left' | 'right', item: CatalogItem] }>()
const image = computed(() => {
  if (props.item.iconDataUrl) return props.item.iconDataUrl
  if (props.item.iconBase64) return props.item.iconBase64.startsWith('data:') ? props.item.iconBase64 : `data:image/png;base64,${props.item.iconBase64}`
  return ''
})
const displayName = computed(() => props.item.name || props.item.fallbackName || props.item.enumName || props.item.id)
const detail = computed(() => [
  displayName.value,
  props.item.enumName || props.item.id,
  props.item.description || '游戏未提供描述',
].join('\n'))
</script>

<template>
  <button
    v-tooltip="detail"
    class="catalog-card"
    :class="{ selected, disabled }"
    :disabled="disabled"
    @click="emit('invoke', 'left', item)"
    @contextmenu.prevent="emit('invoke', 'right', item)"
  >
    <span class="catalog-icon">
      <img v-if="image" :src="image" alt="" />
      <ImageOff v-else :size="24" />
    </span>
    <span class="catalog-copy">
      <strong>{{ displayName }}</strong>
      <small>{{ item.enumName || item.id }}</small>
    </span>
    <span v-if="count && count > 0" class="count-badge">{{ count }}</span>
    <span v-if="selected" class="selected-badge"><Check :size="13" /></span>
  </button>
</template>
