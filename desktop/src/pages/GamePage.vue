<script setup lang="ts">
import { computed } from 'vue'
import { FolderOpen, ShieldCheck, PlugZap, Plug, Trash2, Play, RefreshCw } from '../icons'
import { useAppStore } from '../stores/app'
import { useUiStore } from '../stores/ui'

const store = useAppStore()
const ui = useUiStore()
const pluginState = computed(() => store.snapshot?.plugin?.state ?? 'notInstalled')
const pluginStateLabel = computed(() => ({
  enabled: '已启用', disabled: '已停用', incomplete: '需要修复', notInstalled: '未安装',
}[pluginState.value]))
</script>

<template>
  <div class="page-grid game-page">
    <section class="page-heading">
      <div><span class="eyebrow">SYSTEM</span><h1>游戏与插件</h1></div>
      <button class="button secondary compact" :disabled="ui.busy" @click="store.refreshConnection"><RefreshCw :size="16" />刷新连接</button>
    </section>

    <section class="mechanical-section game-location">
      <header class="section-heading"><div><h2>游戏构建</h2><p>只连接经过路径、产品身份和程序集指纹验证的 Skyspine。</p></div></header>
      <div class="path-row">
        <div class="path-display">
          <span>{{ store.snapshot?.game?.gameRoot || '尚未选择游戏目录' }}</span>
          <small v-if="store.snapshot?.game">{{ store.snapshot.game.productVersion }} · {{ store.snapshot.game.assemblySha256.slice(0, 12) }}</small>
        </div>
        <button class="button primary" :disabled="ui.busy" @click="store.selectGame"><FolderOpen :size="17" />选择目录</button>
      </div>
      <div class="validation-strip" :class="store.snapshot?.game ? 'valid' : 'waiting'">
        <ShieldCheck :size="18" />
        <span>{{ store.snapshot?.game ? '游戏构建与运行时合同已验证' : '选择游戏目录后才能管理插件' }}</span>
      </div>
    </section>

    <section class="mechanical-section plugin-section">
      <header class="section-heading">
        <div><h2>AutoPlayer 插件</h2><p>{{ store.snapshot?.plugin?.detail || '尚未读取插件状态' }}</p></div>
        <span class="status-badge" :class="{ online: pluginState === 'enabled', warning: pluginState === 'incomplete' }">{{ pluginStateLabel }}</span>
      </header>
      <div class="plugin-facts">
        <div><span>插件版本</span><strong>{{ store.snapshot?.plugin?.pluginVersion || '-' }}</strong></div>
        <div><span>BepInEx</span><strong>{{ store.snapshot?.plugin?.bepInExCompatible ? '固定版本已验证' : '未验证' }}</strong></div>
        <div><span>控制通道</span><strong>{{ store.snapshot?.connection.label || '等待 Host' }}</strong></div>
      </div>
      <div class="action-bar">
        <button class="button primary" :disabled="!store.snapshot?.game || ui.busy" @click="store.installPlugin"><PlugZap :size="17" />安装或修复</button>
        <button
          class="button secondary"
          :disabled="!store.snapshot?.plugin || pluginState === 'notInstalled' || ui.busy"
          @click="store.setPluginEnabled(pluginState === 'disabled')"
        ><Plug :size="17" />{{ pluginState === 'disabled' ? '启用插件' : '停用插件' }}</button>
        <button class="button danger compact" :disabled="pluginState === 'notInstalled' || ui.busy" @click="store.uninstallPlugin"><Trash2 :size="16" />卸载</button>
      </div>
    </section>

    <section class="launch-band" :class="{ ready: pluginState === 'enabled' }">
      <div>
        <span class="signal-dot" :class="store.snapshot?.connection.trusted ? 'is-online' : 'is-waiting'" />
        <div><strong>{{ store.snapshot?.connection.label || '等待连接' }}</strong><small>{{ store.snapshot?.connection.reason || '游戏运行后会自动建立可信连接' }}</small></div>
      </div>
      <button class="button launch" :disabled="pluginState !== 'enabled' || ui.busy" @click="store.launchGame"><Play :size="18" fill="currentColor" />启动游戏</button>
    </section>
  </div>
</template>
