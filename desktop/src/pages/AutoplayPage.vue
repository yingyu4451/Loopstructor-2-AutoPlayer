<script setup lang="ts">
import { Bot, OctagonX } from '../icons'
import { useAppStore } from '../stores/app'
const store = useAppStore()
</script>

<template>
  <div class="page-grid autoplay-page">
    <section class="page-heading"><div><span class="eyebrow">AUTOMATION</span><h1>自动游玩</h1></div></section>
    <section class="autoplay-unavailable">
      <div class="disabled-gear"><Bot :size="52" /></div>
      <span class="status-badge warning">暂未开放</span>
      <h2>自动游玩入口已暂停</h2>
      <p>插件和 Core 实现仍随版本保留，本界面不提供配置、开始、轨迹或决策操作。</p>
      <div class="autoplay-runtime-state">
        <span>当前插件状态</span>
        <strong>{{ store.snapshot?.status?.runState || 'standby' }}</strong>
      </div>
      <button v-if="store.snapshot?.connection.autoplayActive" class="button danger" @click="store.stopAutomation">
        <OctagonX :size="18" />停止现有自动游玩
      </button>
    </section>
  </div>
</template>
