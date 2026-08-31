<script setup lang="ts">
import { computed } from 'vue'
import { Shield, Map, Eye, Sparkles, FastForward, Skull, Gift } from '../icons'
import { useAppStore } from '../stores/app'
const store = useAppStore()

const state = computed(() => store.cheatState ?? {})
async function toggle(command: string, value: boolean) {
  if (await store.command(command, { enabled: value })) await store.refreshState()
}
</script>

<template>
  <div class="battle-workspace">
    <section class="battle-section mechanical-section" :class="{ mutationLocked: store.writeLocked }">
      <header class="section-heading"><div><h2>战场状态</h2><p>持久开关会标记当前游戏使用过作弊。</p></div></header>
      <div class="battle-action-grid">
        <label class="action-toggle"><Shield :size="19" /><span><strong>基地无敌</strong><small>阻止基地受到伤害</small></span><input type="checkbox" :checked="state.baseGodMode" :disabled="store.writeLocked" @change="toggle('cheat.setBaseGodMode', ($event.target as HTMLInputElement).checked)" /></label>
        <label class="action-toggle"><Map :size="19" /><span><strong>地图自由跳转</strong><small>允许选择后续地图节点</small></span><input type="checkbox" :checked="state.mapSkipEnabled" :disabled="store.writeLocked" @change="toggle('cheat.setMapSkipEnabled', ($event.target as HTMLInputElement).checked)" /></label>
      </div>
    </section>
    <section class="battle-section mechanical-section">
      <header class="section-heading"><div><h2>敌人信息</h2><p>只读覆盖在自动游玩期间仍可使用。</p></div></header>
      <div class="battle-action-grid">
        <label class="action-toggle"><Eye :size="19" /><span><strong>显示敌人 ID</strong><small>在怪物旁显示运行时 ID</small></span><input type="checkbox" :checked="state.enemyIdsVisible" @change="toggle('cheat.setEnemyIdOverlay', ($event.target as HTMLInputElement).checked)" /></label>
        <label class="action-toggle"><Sparkles :size="19" /><span><strong>显示 Buff</strong><small>显示 Buff 图标与持续时间</small></span><input type="checkbox" :checked="state.enemyBuffsVisible" @change="toggle('cheat.setEnemyBuffOverlay', ($event.target as HTMLInputElement).checked)" /></label>
      </div>
    </section>
    <section class="battle-section mechanical-section" :class="{ mutationLocked: store.writeLocked }">
      <header class="section-heading"><div><h2>波次与阻断恢复</h2><p>这些操作会立即改变当前战斗或奖励流程。</p></div></header>
      <div class="battle-action-grid command-grid">
        <button class="command-button" :disabled="store.writeLocked" @click="store.command('cheat.endWave')"><FastForward :size="20" /><span><strong>结束当前波次</strong><small>推进到本波结算</small></span></button>
        <button class="command-button danger-command" :disabled="store.writeLocked" @click="store.command('cheat.clearEnemies')"><Skull :size="20" /><span><strong>清除所有敌人</strong><small>立即清理当前场景</small></span></button>
        <button class="command-button" :disabled="store.writeLocked" @click="store.command('cheat.skipRewardPopup')"><Gift :size="20" /><span><strong>跳过当前奖励</strong><small>放弃奖励并解除弹窗阻断</small></span></button>
      </div>
    </section>
  </div>
</template>
