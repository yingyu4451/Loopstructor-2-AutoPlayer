<script setup lang="ts">
import { computed } from 'vue'
import { RefreshCw, ShieldAlert } from '../icons'
import { useAppStore } from '../stores/app'
import { useUiStore } from '../stores/ui'

const store = useAppStore()
const ui = useUiStore()
const statusText = computed(() => {
  if (!store.snapshot?.connection.trusted) return '等待游戏连接'
  if (!store.snapshot.connection.cheatAvailable) return '当前游戏版本不可用'
  if (store.writeLocked) return '自动游玩中 · 仅可查看'
  if (store.cheatEnabled) return store.snapshot.status?.cheatUsed ? '作弊已开启 · 本局用过作弊' : '作弊已开启'
  return store.snapshot.status?.cheatUsed ? '作弊已关闭 · 本局用过作弊' : '作弊已关闭'
})
</script>

<template>
  <div class="cheat-control-bar">
    <div class="cheat-control-title">
      <span>作弊运行控制</span>
      <span class="status-badge" :class="{ online: store.cheatEnabled, warning: store.writeLocked }">{{ statusText }}</span>
    </div>
    <div v-if="store.writeLocked" class="lock-message">
      <ShieldAlert :size="16" /> 自动游玩正在运行：停止自动游玩后恢复修改；目录和敌人信息仍可查看。
    </div>
    <div class="cheat-control-actions">
      <button class="icon-button" aria-label="刷新全部作弊目录" :disabled="!store.connected || ui.busy" @click="store.refreshCheat()">
        <RefreshCw :size="17" />
      </button>
      <label class="switch-control" :class="{ disabled: !store.snapshot?.connection.cheatAvailable }">
        <input
          type="checkbox"
          :checked="store.cheatEnabled"
          :disabled="!store.snapshot?.connection.cheatAvailable || ui.busy"
          @change="store.setCheatEnabled(($event.target as HTMLInputElement).checked)"
        />
        <span class="switch-track"><span /></span>
        <span>开启作弊</span>
      </label>
    </div>
  </div>
</template>
