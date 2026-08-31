<script setup lang="ts">
import { X } from '../icons'
import { useUiStore } from '../stores/ui'
const ui = useUiStore()
</script>

<template>
  <div v-if="ui.confirmDialog" class="modal-backdrop" @click.self="ui.resolveConfirm(false)">
    <section class="mechanical-modal" role="dialog" aria-modal="true" :aria-label="ui.confirmDialog.title">
      <header>
        <h2>{{ ui.confirmDialog.title }}</h2>
        <button class="icon-button" aria-label="关闭" @click="ui.resolveConfirm(false)"><X :size="18" /></button>
      </header>
      <p>{{ ui.confirmDialog.message }}</p>
      <footer>
        <button class="button secondary" @click="ui.resolveConfirm(false)">{{ ui.confirmDialog.cancelText }}</button>
        <button class="button" :class="ui.confirmDialog.danger ? 'danger' : 'primary'" @click="ui.resolveConfirm(true)">{{ ui.confirmDialog.confirmText }}</button>
      </footer>
    </section>
  </div>
</template>
