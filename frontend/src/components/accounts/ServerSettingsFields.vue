<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { DEFAULT_PORTS, type ImapSecurity } from '@/stores/accounts'
import type { ServerSettings } from './serverSettings'

defineProps<{
  errors: Record<string, string>
  /** Editing an existing account: the password may stay empty. */
  passwordOptional?: boolean
}>()

const settings = defineModel<ServerSettings>({ required: true })

const SECURITY_OPTIONS: ImapSecurity[] = ['SslOnConnect', 'StartTls', 'None']

function setSecurity(value: unknown) {
  const security = value as ImapSecurity
  // Follow the standard port unless the user picked a custom one.
  if (settings.value.port === DEFAULT_PORTS[settings.value.security]) {
    settings.value.port = DEFAULT_PORTS[security]
  }
  settings.value.security = security
}
</script>

<template>
  <div class="grid gap-4">
    <FormField id="account-name" v-slot="aria" :label="$t('accounts.fields.name')" :hint="$t('accounts.fields.nameHint')" :error="errors.name">
      <Input id="account-name" v-model="settings.name" v-bind="aria" autocomplete="off" />
    </FormField>

    <div class="grid gap-4 sm:grid-cols-[1fr_8rem]">
      <FormField id="account-host" v-slot="aria" :label="$t('accounts.fields.host')" :error="errors.host">
        <Input
          id="account-host"
          v-model.trim="settings.host"
          v-bind="aria"
          :placeholder="$t('accounts.fields.hostPlaceholder')"
          autocomplete="off"
          autocapitalize="off"
          spellcheck="false"
        />
      </FormField>
      <FormField id="account-port" v-slot="aria" :label="$t('accounts.fields.port')" :error="errors.port">
        <Input id="account-port" v-model.number="settings.port" v-bind="aria" type="number" min="1" max="65535" />
      </FormField>
    </div>

    <fieldset class="grid gap-2">
      <legend class="mb-2 text-sm font-medium">{{ $t('accounts.fields.security') }}</legend>
      <RadioGroup :model-value="settings.security" class="grid gap-3" @update:model-value="setSecurity">
        <div v-for="option in SECURITY_OPTIONS" :key="option" class="flex items-start gap-3">
          <RadioGroupItem :id="`security-${option}`" :value="option" class="mt-0.5" />
          <Label :for="`security-${option}`" class="grid gap-1 font-normal">
            <span class="font-medium">{{ $t(`accounts.security.${option}`) }}</span>
            <span class="text-muted-foreground text-xs">{{ $t(`accounts.security.${option}Hint`) }}</span>
          </Label>
        </div>
      </RadioGroup>
    </fieldset>

    <FormField
      id="account-username"
      v-slot="aria"
      :label="$t('accounts.fields.username')"
      :hint="$t('accounts.fields.usernameHint')"
      :error="errors.username"
    >
      <Input
        id="account-username"
        v-model.trim="settings.username"
        v-bind="aria"
        autocomplete="off"
        autocapitalize="off"
        spellcheck="false"
      />
    </FormField>
    <FormField
      id="account-password"
      v-slot="aria"
      :label="$t('accounts.fields.password')"
      :hint="passwordOptional ? $t('accounts.fields.passwordKeep') : undefined"
      :error="errors.password"
    >
      <!-- "new-password" stops browsers from filling in the mailnix login password. -->
      <Input id="account-password" v-model="settings.password" v-bind="aria" type="password" autocomplete="new-password" />
    </FormField>
  </div>
</template>
