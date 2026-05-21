import type { ProductStatus } from '../api/products'

interface Props {
  status: ProductStatus
}

const CONFIG: Record<ProductStatus, { label: string; cls: string }> = {
  'In Stock':    { label: 'Còn hàng',   cls: 'bg-green-100 text-green-700 border border-green-200' },
  'Low Stock':   { label: 'Tồn thấp',   cls: 'bg-yellow-100 text-yellow-700 border border-yellow-200' },
  'Out of Stock':{ label: 'Hết hàng',   cls: 'bg-red-100 text-red-700 border border-red-200' },
}

export default function StatusBadge({ status }: Props) {
  const cfg = CONFIG[status] ?? CONFIG['In Stock']
  return (
    <span className={`inline-block rounded-full px-2.5 py-0.5 text-xs font-semibold ${cfg.cls}`}>
      {cfg.label}
    </span>
  )
}
