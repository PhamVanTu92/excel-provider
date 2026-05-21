import { apiFetch } from './client'

export type Region = 'North' | 'South' | 'East' | 'West' | 'Central'
export type Channel = 'Online' | 'Store'

export interface SaleRecord {
  id: number
  date: string       // yyyy-MM-dd
  region: Region
  product: string
  category: string
  revenue: number
  units: number
  channel: Channel
}

export type SaleInput = Omit<SaleRecord, 'id'>

export const REGION_LABELS: Record<Region, string> = {
  North:   'Bắc',
  South:   'Nam',
  East:    'Đông',
  West:    'Tây',
  Central: 'Trung',
}

export const REGIONS: Region[] = ['North', 'South', 'East', 'West', 'Central']
export const CHANNELS: Channel[] = ['Online', 'Store']

export function getSales(params: { date?: string; region?: string }): Promise<SaleRecord[]> {
  const qs = new URLSearchParams()
  if (params.date)   qs.set('date',   params.date)
  if (params.region) qs.set('region', params.region)
  const query = qs.toString() ? `?${qs.toString()}` : ''
  return apiFetch<SaleRecord[]>(`/api/sales${query}`)
}

export function createSale(data: SaleInput): Promise<SaleRecord> {
  return apiFetch<SaleRecord>('/api/sales', {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function updateSale(id: number, data: SaleInput): Promise<SaleRecord> {
  return apiFetch<SaleRecord>(`/api/sales/${id}`, {
    method: 'PUT',
    body: JSON.stringify({ id, ...data }),
  })
}

export function deleteSale(id: number): Promise<void> {
  return apiFetch<void>(`/api/sales/${id}`, { method: 'DELETE' })
}
