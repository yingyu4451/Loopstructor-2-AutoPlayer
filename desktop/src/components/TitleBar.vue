<script setup lang="ts">
import { computed } from 'vue'
import { Minus, Square, X, Download } from '../icons'
import managerLogo from '../../../assets/branding/manager-logo-256.png'
import { useAppStore } from '../stores/app'

const store = useAppStore()
const api = window.loopstructorDesktop
const props = defineProps<{ updaterMode?: boolean }>()
const connectionClass = computed(() => store.snapshot?.connection.trusted ? 'is-online' : 'is-waiting')

function systemMenu(event: MouseEvent) {
  api.showSystemMenu({ x: event.screenX, y: event.screenY })
}
</script>

<template>
  <header class="titlebar" @contextmenu.prevent="systemMenu">
    <div class="title-drive" aria-hidden="true">
      <span class="title-cog cog-large" />
      <span class="title-cog cog-small" />
      <span class="drive-shaft" />
    </div>
    <div class="titlebar-brand">
      <img :src="managerLogo" alt="" width="42" height="42" class="brand-logo" fetchpriority="high" />
      <div>
        <strong translate="no">Loopstructor 2 QA Tool</strong>
        <span>v{{ store.snapshot?.version ?? '0.6.67' }}</span>
      </div>
    </div>
    <div class="titlebar-status">
      <span class="plaque-bolt" aria-hidden="true" />
      <span class="signal-dot" :class="connectionClass" />
      <span class="status-copy">{{ props.updaterMode ? '更新事务' : (store.snapshot?.connection.label ?? '正在启动 Host') }}</span>
      <button
        v-if="store.snapshot?.update?.updateAvailable"
        v-tooltip="'点击安装更新'"
        class="update-chip"
        aria-label="安装可用更新"
        @click="store.installUpdate()"
      >
        <Download :size="14" /> v{{ store.snapshot.update.latestVersion }} 可用
      </button>
      <span class="plaque-bolt" aria-hidden="true" />
    </div>
    <div class="window-controls">
      <button v-tooltip="'最小化'" aria-label="最小化" @click="api.minimize()"><Minus :size="18" /></button>
      <button v-tooltip="'最大化或还原'" aria-label="最大化或还原" @click="api.toggleMaximize()"><Square :size="15" /></button>
      <button v-tooltip="'关闭'" class="close" aria-label="关闭" @click="api.close()"><X :size="18" /></button>
    </div>
  </header>
</template>
