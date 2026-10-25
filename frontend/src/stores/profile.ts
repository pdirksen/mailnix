import { api, type Schemas, unwrap } from '@/api/client'
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useAuthStore } from './auth'

export type Profile = Schemas['ProfileResponse']
export type ProfileUpdate = Schemas['UpdateProfileRequest']

/** The signed-in user's own settings (Settings pages). */
export const useProfileStore = defineStore('profile', () => {
  const profile = ref<Profile | null>(null)

  async function load() {
    profile.value = unwrap(await api.GET('/api/profile'))
    return profile.value
  }

  /** Keeps the signed-in user (name in the menu, language) in step with the profile. */
  function apply(updated: Profile) {
    profile.value = updated
    const auth = useAuthStore()
    if (auth.user) {
      auth.setUser({
        ...auth.user,
        displayName: updated.displayName,
        timeZone: updated.timeZone,
        language: updated.language,
      })
    }
  }

  async function update(changes: ProfileUpdate) {
    apply(unwrap(await api.PUT('/api/profile', { body: changes })))
  }

  async function saveAddresses(addresses: string[]) {
    apply(unwrap(await api.PUT('/api/profile/addresses', { body: { addresses } })))
  }

  async function changePassword(currentPassword: string, newPassword: string) {
    unwrap(await api.POST('/api/profile/password', { body: { currentPassword, newPassword } }))
  }

  async function signOutOthers() {
    unwrap(await api.POST('/api/profile/sign-out-others'))
  }

  async function startTwoFactorSetup() {
    return unwrap(await api.POST('/api/profile/two-factor/setup'))
  }

  async function enableTwoFactor(code: string) {
    const result = unwrap(await api.POST('/api/profile/two-factor/enable', { body: { code } }))
    await load()
    return result.codes
  }

  async function disableTwoFactor(password: string) {
    unwrap(await api.POST('/api/profile/two-factor/disable', { body: { password } }))
    await load()
  }

  async function newRecoveryCodes(password: string) {
    const result = unwrap(await api.POST('/api/profile/two-factor/recovery-codes', { body: { password } }))
    await load()
    return result.codes
  }

  async function deleteAccount(password: string) {
    unwrap(await api.POST('/api/profile/delete', { body: { password } }))
    profile.value = null
    useAuthStore().clear()
  }

  return {
    profile,
    load,
    update,
    saveAddresses,
    changePassword,
    signOutOthers,
    startTwoFactorSetup,
    enableTwoFactor,
    disableTwoFactor,
    newRecoveryCodes,
    deleteAccount,
  }
})
