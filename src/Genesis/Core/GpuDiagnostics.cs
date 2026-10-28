using System;
using System.Linq;
using System.Text;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;

namespace Genesis.Core
{
    /// <summary>
    /// Диагностика выбора ускорителя ILGPU.
    ///
    /// Нужна потому, что ILGPU молча отбрасывает OpenCL-устройства, не прошедшие
    /// внутренний фильтр, и остаётся только CPUAccelerator — без единого сообщения.
    /// </summary>
    public static class GpuDiagnostics
    {
        /// <summary>
        /// Возвращает человекочитаемый отчёт: какие устройства видит ILGPU и почему
        /// GPU не попал в список, если он есть в системе.
        /// </summary>
        public static string Describe()
        {
            var sb = new StringBuilder();

            using (var ctx = Context.CreateDefault())
            {
                var devices = ctx.Devices;
                sb.AppendLine($"ILGPU видит устройств: {devices.Length}");
                foreach (var d in devices)
                    sb.AppendLine($"  • {d.Name} [{d.AcceleratorType}]");

                if (devices.Any(d => d.AcceleratorType != AcceleratorType.CPU))
                    return sb.ToString();
            }

            // GPU в списке нет. Пробуем OpenCL без фильтра ILGPU, чтобы понять,
            // устройство отсутствует или оно есть, но не подходит.
            sb.AppendLine("GPU в списке нет. Проверяю OpenCL-устройства без фильтра ILGPU:");

            try
            {
                using var openCl = Context.Create(b => b.OpenCL(_ => true));

                // ILGPU подставляет CPUDevice, если не нашлось ни одного устройства,
                // поэтому CPU здесь не показатель — смотрим только на не-CPU.
                var openClDevices = openCl.Devices
                    .Where(d => d.AcceleratorType != AcceleratorType.CPU)
                    .ToArray();

                if (openClDevices.Length == 0)
                {
                    sb.AppendLine("  OpenCL-устройств не найдено (нет ни одной платформы/драйвера).");
                }
                else
                {
                    foreach (var d in openClDevices)
                    {
                        try
                        {
                            using var accelerator = d.CreateAccelerator(openCl);
                            sb.AppendLine($"  • {d.Name}: поддерживается");
                        }
                        catch (Exception e)
                        {
                            sb.AppendLine($"  • {d.Name}: отклонено ILGPU — {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"  OpenCL-бэкенд недоступен: {e.Message}");
            }

            sb.AppendLine();
            sb.AppendLine("ILGPU требует OpenCL C 2.0+ и generic address space.");
            sb.AppendLine("Mesa Rusticl (Linux) даёт OpenCL C 1.2 без generic address space и не подходит.");
            sb.AppendLine("Linux/AMD: установите ROCm OpenCL (пакет opencl-rocm / rocm-opencl-runtime).");
            sb.AppendLine("Windows/AMD: достаточно драйвера Adrenalin. Windows/NVIDIA: CUDA работает из коробки.");

            return sb.ToString();
        }
    }
}
