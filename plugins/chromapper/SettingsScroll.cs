using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper
{
    internal sealed class SettingsScroll : MonoBehaviour
    {
        private ScrollRect scroll;
        private GameObject lastSelected;

        public static void AddTo(DialogLayout layout, Scrollbar template)
        {
            var area = layout.Slot("Scroll");
            var scroller = area.gameObject.AddComponent<ScrollRect>();
            scroller.content = layout.Slot("Content");
            scroller.viewport = layout.Slot("Viewport");
            scroller.horizontal = false;
            scroller.inertia = false;
            scroller.movementType = ScrollRect.MovementType.Clamped;
            scroller.scrollSensitivity = 24;
            if (template != null)
                scroller.verticalScrollbar = AddNativeScrollbar(layout.Slot("Scrollbar"), template);
            scroller.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            area.gameObject.AddComponent<SettingsScroll>().scroll = scroller;
            scroller.verticalNormalizedPosition = 1;
        }

        private static Scrollbar AddNativeScrollbar(RectTransform slot, Scrollbar template)
        {
            // The dropdown template uses the same sliced track and thumb as Options and Song Select.
            var scrollbar = Instantiate(template, slot, false);
            DialogLayout.Fill((RectTransform)scrollbar.transform, slot);
            scrollbar.GetComponent<Image>().color = new Color(.301887f, .301887f, .301887f);
            scrollbar.targetGraphic.color = new Color(.162647f, .628768f, .801887f);
            var navigation = scrollbar.navigation;
            navigation.mode = Navigation.Mode.None;
            scrollbar.navigation = navigation;
            scrollbar.SetValueWithoutNotify(1);
            return scrollbar;
        }

        private void LateUpdate()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == lastSelected) return;
            lastSelected = selected;
            if (selected == null || !selected.transform.IsChildOf(scroll.content)) return;

            var viewport = scroll.viewport.rect;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, selected.transform);
            float delta = bounds.min.y < viewport.yMin ? viewport.yMin - bounds.min.y :
                bounds.max.y > viewport.yMax ? viewport.yMax - bounds.max.y : 0;
            if (delta == 0) return;
            scroll.StopMovement();
            var position = scroll.content.anchoredPosition;
            float maxOffset = Mathf.Max(0, scroll.content.rect.height - viewport.height);
            position.y = Mathf.Clamp(position.y + delta, 0, maxOffset);
            scroll.content.anchoredPosition = position;
        }
    }
}
