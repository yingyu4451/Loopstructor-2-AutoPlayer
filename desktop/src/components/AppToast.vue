<script setup lang="ts">
import { CheckCircle2, Info, TriangleAlert, XCircle } from '../icons'
import { useUiStore } from '../stores/ui'
const ui = useUiStore()
const icons = { success: CheckCircle2, info: Info, warning: TriangleAlert, error: XCircle }
</script>

<template>
  <Transition name="toast-slide">
    <div v-if="ui.activeToast" class="app-toast" :class="ui.activeToast.kind" role="status">
      <component :is="icons[ui.activeToast.kind]" :size="18" />
      <span>{{ ui.activeToast.message }}</span>
    </div>
  </Transition>
</template>
