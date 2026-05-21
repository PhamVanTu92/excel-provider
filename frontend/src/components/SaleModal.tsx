import { useState, useEffect } from 'react'
import type { SaleRecord, SaleInput, Region, Channel } from '../api/sales'
import { REGIONS, CHANNELS, REGION_LABELS } from '../api/sales'

interface Props {
  open: boolean
  initial?: SaleRecord | null
  onSave: (data: SaleInput) => Promise<void>
  onClose: () => void
}

const EMPTY: SaleInput = {
  date:     new Date().toISOString().slice(0, 10),
  region:   'North',
  product:  '',
  category: '',
  revenue:  0,
  units:    1,
  channel:  'Online',
}

export default function SaleModal({ open, initial, onSave, onClose }: Props) {
  const [form, setForm] = useState<SaleInput>(EMPTY)
  const [errors, setErrors] = useState<Partial<Record<keyof SaleInput, string>>>({})
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (open) {
      setForm(initial ? { ...initial } : { ...EMPTY, date: new Date().toISOString().slice(0, 10) })
      setErrors({})
    }
  }, [open, initial])

  if (!open) return null

  function set<K extends keyof SaleInput>(key: K, value: SaleInput[K]) {
    setForm(f => ({ ...f, [key]: value }))
    setErrors(e => ({ ...e, [key]: undefined }))
  }

  function validate(): boolean {
    const e: typeof errors = {}
    if (!form.date)    e.date    = 'Vui lòng chọn ngày'
    if (!form.product.trim()) e.product = 'Vui lòng nhập tên sản phẩm'
    if (!form.category.trim()) e.category = 'Vui lòng nhập danh mục'
    if (form.revenue < 0)   e.revenue = 'Doanh thu không âm'
    if (form.units < 1)     e.units   = 'Số lượng tối thiểu là 1'
    setErrors(e)
    return Object.keys(e).length === 0
  }

  async function handleSubmit(ev: React.FormEvent) {
    ev.preventDefault()
    if (!validate()) return
    setSaving(true)
    try {
      await onSave(form)
      onClose()
    } finally {
      setSaving(false)
    }
  }

  const isEdit = !!initial

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-xl bg-white shadow-2xl">
        <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4">
          <h2 className="text-base font-semibold text-slate-800">
            {isEdit ? 'Sửa bản ghi bán hàng' : 'Thêm bản ghi bán hàng'}
          </h2>
          <button onClick={onClose} className="text-slate-400 hover:text-slate-600 text-xl leading-none" aria-label="Đóng">×</button>
        </div>

        <form onSubmit={handleSubmit} noValidate>
          <div className="grid grid-cols-2 gap-4 px-6 py-5">
            {/* Date */}
            <div>
              <label className="label">Ngày</label>
              <input
                type="date"
                className="input"
                value={form.date}
                onChange={e => set('date', e.target.value)}
              />
              {errors.date && <p className="mt-1 text-xs text-red-500">{errors.date}</p>}
            </div>

            {/* Region */}
            <div>
              <label className="label">Khu vực</label>
              <select
                className="input"
                value={form.region}
                onChange={e => set('region', e.target.value as Region)}
              >
                {REGIONS.map(r => (
                  <option key={r} value={r}>{REGION_LABELS[r]}</option>
                ))}
              </select>
            </div>

            {/* Product */}
            <div className="col-span-2">
              <label className="label">Sản phẩm</label>
              <input
                type="text"
                className="input"
                placeholder="Tên sản phẩm"
                value={form.product}
                onChange={e => set('product', e.target.value)}
              />
              {errors.product && <p className="mt-1 text-xs text-red-500">{errors.product}</p>}
            </div>

            {/* Category */}
            <div className="col-span-2">
              <label className="label">Danh mục</label>
              <input
                type="text"
                className="input"
                placeholder="Danh mục sản phẩm"
                value={form.category}
                onChange={e => set('category', e.target.value)}
              />
              {errors.category && <p className="mt-1 text-xs text-red-500">{errors.category}</p>}
            </div>

            {/* Revenue */}
            <div>
              <label className="label">Doanh thu (VNĐ)</label>
              <input
                type="number"
                className="input"
                min={0}
                step={1000}
                value={form.revenue}
                onChange={e => set('revenue', Number(e.target.value))}
              />
              {errors.revenue && <p className="mt-1 text-xs text-red-500">{errors.revenue}</p>}
            </div>

            {/* Units */}
            <div>
              <label className="label">Số lượng</label>
              <input
                type="number"
                className="input"
                min={1}
                value={form.units}
                onChange={e => set('units', Number(e.target.value))}
              />
              {errors.units && <p className="mt-1 text-xs text-red-500">{errors.units}</p>}
            </div>

            {/* Channel */}
            <div>
              <label className="label">Kênh bán</label>
              <select
                className="input"
                value={form.channel}
                onChange={e => set('channel', e.target.value as Channel)}
              >
                {CHANNELS.map(c => (
                  <option key={c} value={c}>{c === 'Online' ? 'Trực tuyến' : 'Cửa hàng'}</option>
                ))}
              </select>
            </div>
          </div>

          <div className="flex justify-end gap-2 border-t border-slate-100 px-6 py-4">
            <button type="button" className="btn-secondary" onClick={onClose} disabled={saving}>
              Huỷ
            </button>
            <button type="submit" className="btn-primary" disabled={saving}>
              {saving ? 'Đang lưu…' : isEdit ? 'Cập nhật' : 'Thêm'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
