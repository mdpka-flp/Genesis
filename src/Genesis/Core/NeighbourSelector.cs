using System;

namespace Genesis.Core
{
    /// <summary>
    /// Выбор K ближайших соседей каждого типа для образования новых связей.
    ///
    /// Часть связей уже существует и приходит в <see cref="Begin"/> как занятые
    /// слоты (<see cref="Lock"/>): их нельзя вытеснить, можно только заполнять
    /// свободные места. Поэтому набор партнёров не пересобирается каждый кадр —
    /// структура держится, пока связь не растянется или не изменится правило.
    ///
    /// Экземпляр живёт в [ThreadStatic]-поле симуляции, поэтому массивов на кадр
    /// не выделяется. Один экземпляр = буфер на одну частицу.
    /// </summary>
    internal sealed class NeighbourSelector
    {
        private float[] _distance = Array.Empty<float>();
        private float[] _dirX = Array.Empty<float>();
        private float[] _dirY = Array.Empty<float>();
        private int[] _index = Array.Empty<int>();

        // Служебное состояние по типам соседей (не по слотам): сколько уже выбрано,
        // сколько из них — существующие связи, и какой новый слот самый «плохой».
        private float[] _worst = Array.Empty<float>();
        private int[] _worstSlot = Array.Empty<int>();
        private int[] _count = Array.Empty<int>();
        private int[] _locked = Array.Empty<int>();

        public int[] Indexes => _index;
        public float[] DirX => _dirX;
        public float[] DirY => _dirY;
        public float[] Distances => _distance;
        public int Count(int type) => _count[type];
        public int LockedCount(int type) => _locked[type];

        /// <summary>
        /// Готовит буфер к обработке очередной частицы. <paramref name="slots"/> —
        /// суммарное число слотов её типа (из <c>slotTotal</c>).
        /// </summary>
        public void Begin(int slots, int typeCount)
        {
            if (_distance.Length < slots)
            {
                _distance = new float[slots];
                _dirX = new float[slots];
                _dirY = new float[slots];
                _index = new int[slots];
            }

            if (_count.Length < typeCount)
            {
                _worst = new float[typeCount];
                _worstSlot = new int[typeCount];
                _count = new int[typeCount];
                _locked = new int[typeCount];
            }

            Array.Clear(_count, 0, typeCount);
            Array.Clear(_worst, 0, typeCount);
            Array.Clear(_locked, 0, typeCount);
        }

        /// <summary>
        /// Отмечает, что <paramref name="count"/> слотов этого типа уже заняты
        /// существующими связями: они остаются в начале блока и не вытесняются.
        /// </summary>
        public void Lock(int type, int count)
        {
            _locked[type] = count;
            _count[type] = count;
            _worst[type] = 0f;
        }

        /// <summary>
        /// Предлагает соседа в набор своего типа. Сосед занимает свободный слот;
        /// если все слоты заняты новыми кандидатами, он может вытеснить самого
        /// дальнего из них. Уже существующие связи не вытесняются никогда.
        /// </summary>
        public bool Offer(
            int type,
            int partner,
            float dirX,
            float dirY,
            float distance,
            int limit,
            int slotBase)
        {
            int count = _count[type];
            int locked = _locked[type];

            if (count < limit)
            {
                int slot = slotBase + count;
                _distance[slot] = distance;
                _dirX[slot] = dirX;
                _dirY[slot] = dirY;
                _index[slot] = partner;

                if (count == locked)
                {
                    _worst[type] = distance;
                    _worstSlot[type] = count;
                }
                else if (distance > _worst[type])
                {
                    _worst[type] = distance;
                    _worstSlot[type] = count;
                }

                _count[type] = count + 1;
                return true;
            }

            // Свободных слотов нет: существующие связи трогать нельзя, а среди
            // новых можно заменить только самого дальнего.
            if (count == locked)
                return false;

            if (distance >= _worst[type])
                return false;

            int worst = slotBase + _worstSlot[type];
            _distance[worst] = distance;
            _dirX[worst] = dirX;
            _dirY[worst] = dirY;
            _index[worst] = partner;

            // Новый «худший» — только среди новых слотов; существующие связи
            // начинаются с индекса locked и в пересчёт не входят.
            float worstDistance = _distance[slotBase + locked];
            int worstSlot = locked;
            for (int k = locked + 1; k < limit; k++)
            {
                float d = _distance[slotBase + k];
                if (d > worstDistance)
                {
                    worstDistance = d;
                    worstSlot = k;
                }
            }

            _worst[type] = worstDistance;
            _worstSlot[type] = worstSlot;
            return true;
        }
    }
}
