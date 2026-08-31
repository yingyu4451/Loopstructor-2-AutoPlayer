import eslint from '@eslint/js'
import tseslint from 'typescript-eslint'
import pluginVue from 'eslint-plugin-vue'

export default tseslint.config(
  { ignores: ['dist/**', 'dist-main/**', 'dist-electron/**', 'node_modules/**'] },
  eslint.configs.recommended,
  ...tseslint.configs.recommended,
  ...pluginVue.configs['flat/recommended'],
  {
    files: ['src/**/*.{ts,vue}'],
    languageOptions: {
      parserOptions: { parser: tseslint.parser },
      globals: {
        console: 'readonly', crypto: 'readonly', document: 'readonly', HTMLElement: 'readonly',
        MouseEvent: 'readonly', ResizeObserver: 'readonly', structuredClone: 'readonly', window: 'readonly',
      },
    },
  },
  {
    files: ['electron/**/*.cts', '*.config.{js,ts}', 'vite.config.ts'],
    languageOptions: {
      globals: {
        __dirname: 'readonly', Buffer: 'readonly', console: 'readonly', module: 'readonly',
        process: 'readonly', require: 'readonly', setTimeout: 'readonly', clearTimeout: 'readonly',
      },
    },
  },
  {
    rules: {
      'vue/multi-word-component-names': 'off',
      'vue/html-self-closing': 'off',
      'vue/max-attributes-per-line': 'off',
      'vue/multiline-html-element-content-newline': 'off',
      'vue/singleline-html-element-content-newline': 'off',
      'vue/require-default-prop': 'off',
      '@typescript-eslint/no-explicit-any': 'off',
    },
  },
)
