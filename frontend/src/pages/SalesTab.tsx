import { useState, useMemo, useCallback } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getSales, createSale, updateSale, deleteSale,
  REGIONS, REGION_LABELS,
  type SaleRecord, type SaleInput, type Region,
} from '../api/sales'
import SaleModal from '../components/SaleModal'
import DeleteConfirm from '../components/DeleteConfirm'
import type { ToastMessage } from '../components/Toast'

interface Props {
  addToast: (type: ToastMessage['type'], text: string) => void
}

const PAGE_SIZE = 50

export default function SalesTab({ addToast }: Props) {
  const qc = useQueryClient()
  const [filterDate,   setFilterDate]   = useState('')
  const [filterRegion, setFilterRegion] = useState('')
  const [page, setPage] = useState(1)

  const [modalOpen, setModalOpen]   = useState(false)
  const [editTarget, setEditTarget] = useState<SaleRecord | null>(null)

  const [deleteTarget, setDeleteTarget] = useState<SaleRecord | null>(null)

  const { data: sales = [], isLoading, isError } = useQuery({
    queryKey: ['sales', filterDate, filterRegion],
    queryFn: () => getSales({ date: filterDate || undefined, region: filterRegion || undefined }),
  })

  const totalPages = Math.max(1, Math.ceil(sales.length / PAGE_SIZE))
  const paginated  = useMemo(() => sales.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE), [sales, page])

  const createMut = useMutation({
    mutationFn: (data: SaleInput) => createSale(data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['sales'] })
      addToast('success', 'Thêm bản ghi thành công')
    },
    onError: (err: Error) => addToast('error', `Lỗi: ${err.message}`),
  })

  const updateMut = useMutation({
    mutationFn: ({ id, data }: { id: number; data: SaleInput }) => updateSale(id, data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['sales'] })
      addToast('success', 'Cập nhật thành công')
    },
    onError: (err: Error) => addToast('error', `Lỗi: ${err.message}`),
  })

  const deleteMut = useMutation({
    mutationFn: (id: number) => deleteSale(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['sales'] })
      addToast('success', 'Đã xoá bản ghi')
      setDeleteTarget(null)
    },
    onError: (err: Error) => addToast('error', `Lỗi: ${err.message}`),
  })

  const handleSave = useCallback(async (data: SaleInput) => {
    if (editTarget) {
      await updateMut.mutateAsync({ id: editTarget.id, data })
    } else {
      await createMut.mutateAsync(data)
    }
  }, [editTarget, updateMut, createMut])

  function openAdd() {
    setEditTarget(null)
    setModalOpen(true)
  }

  function openEdit(row: SaleRecord) {
    setEditTarget(row)
    setModalOpen(true)
  }

  return (
    <div className="space-y-4">
      {/* Filter bar */}
      <div className="flex flex-wrap items-center gap-3">
        <div>
          <label className="label">Ngày</label>
          <input
            type="date"
            className="input w-44"
            value={filterDate}
            onChange={e => { setFilterDate(e.target.value); setPage(1) }}
          />
        </div>
        <div>
          <label className="label">Khu vực</label>
          <select
            className="input w-40"
            value={filterRegion}
            onChange={e => { setFilterRegion(e.target.value); setPage(1) }}
          >
            <option value="">Tất cả</option>
            {REGIONS.map(r => (
              <option key={r} value={r}>{REGION_LABELS[r]}</option>
            ))}
          </select>
        </div>
        <div className="flex-1" />
        <div className="self-end">
          <button className="btn-primary" onClick={openAdd}>
            + Thêm bản ghi
          </button>
        </div>
      </div>

      {/* Table */}
      <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden">
        {isLoading && (
          <div className="py-12 text-center text-sm text-slate-500">Đang tải…</div>
        )}
        {isError && (
          <div className="py-12 text-center text-sm text-red-500">Không thể tải dữ liệu. Kiểm tra kết nối API.</div>
        )}
        {!isLoading && !isError && (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-200">
              <thead className="bg-slate-50">
                <tr>
                  {['Ngày', 'Khu vực', 'Sản phẩm', 'Danh mục', 'Doanh thu', 'SL', 'Kênh', 'Thao tác'].map(h => (
                    <th key={h} className="table-th">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {paginated.length === 0 && (
                  <tr>
                    <td colSpan={8} className="py-10 text-center text-sm text-slate-400">
                      Không có dữ liệu
                    </td>
                  </tr>
                )}
                {paginated.map(row => (
                  <tr key={row.id} className="hover:bg-slate-50 transition-colors">
                    <td className="table-td font-mono text-xs">{row.date}</td>
                    <td className="table-td">
                      <span className="rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
                        {REGION_LABELS[row.region as Region] ?? row.region}
                      </span>
                    </td>
                    <td className="table-td font-medium">{row.product}</td>
                    <td className="table-td text-slate-500">{row.category}</td>
                    <td className="table-td text-right font-semibold text-green-700">
                      {row.revenue.toLocaleString('vi-VN')}₫
                    </td>
                    <td className="table-td text-right">{row.units}</td>
                    <td className="table-td">
                      <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${
                        row.channel === 'Online'
                          ? 'bg-purple-50 text-purple-700'
                          : 'bg-orange-50 text-orange-700'
                      }`}>
                        {row.channel === 'Online' ? 'Trực tuyến' : 'Cửa hàng'}
                      </span>
                    </td>
                    <td className="table-td">
                      <div className="flex gap-1">
                        <button
                          className="btn-ghost text-xs px-2 py-1"
                          onClick={() => openEdit(row)}
                        >
                          Sửa
                        </button>
                        <button
                          className="btn text-xs px-2 py-1 text-red-600 hover:bg-red-50 focus:ring-red-300"
                          onClick={() => setDeleteTarget(row)}
                        >
                          Xoá
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between text-sm text-slate-500">
          <span>
            Hiển thị {(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, sales.length)} / {sales.length} bản ghi
          </span>
          <div className="flex gap-1">
            <button
              className="btn-secondary px-3 py-1 text-xs"
              disabled={page === 1}
              onClick={() => setPage(p => Math.max(1, p - 1))}
            >
              ← Trước
            </button>
            <span className="flex items-center px-3 text-xs">
              {page} / {totalPages}
            </span>
            <button
              className="btn-secondary px-3 py-1 text-xs"
              disabled={page === totalPages}
              onClick={() => setPage(p => Math.min(totalPages, p + 1))}
            >
              Sau →
            </button>
          </div>
        </div>
      )}

      {/* Modals */}
      <SaleModal
        open={modalOpen}
        initial={editTarget}
        onSave={handleSave}
        onClose={() => setModalOpen(false)}
      />

      <DeleteConfirm
        open={!!deleteTarget}
        message={
          deleteTarget
            ? `Bạn có chắc muốn xoá bản ghi "${deleteTarget.product}" ngày ${deleteTarget.date}?`
            : ''
        }
        onConfirm={() => deleteTarget && deleteMut.mutate(deleteTarget.id)}
        onCancel={() => setDeleteTarget(null)}
        loading={deleteMut.isPending}
      />
    </div>
  )
}
